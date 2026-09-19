using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeBuddy
{
    // Reading the Claude Code CLI's stored login out of the macOS login Keychain.
    //
    // **This is a P/Invoke into Security.framework and not a shell out to
    // `security find-generic-password`, and the difference is the entire user
    // grant.** The consent dialog macOS raises names the *calling binary*. Going
    // through the command-line tool would put "security" on that screen, and an
    // "Always Allow" answered there would grant `/usr/bin/security` — every
    // script on the machine — rather than Claude Buddy. CB-164 rejected minting
    // our own OAuth token precisely because the consent screen would have named
    // the wrong application; reaching the Keychain through a helper binary is the
    // same mistake wearing different clothes, and it would be worse, because the
    // grant it hands out is broader than the one the user thinks they are giving.
    //
    // MacOSScreenLock is the precedent for the mechanics: DllImport against the
    // framework's absolute path, CoreFoundation objects released by hand, the
    // whole class excluded from coverage because a CI runner has neither the item
    // nor anyone to answer the prompt.
    //
    // Two queries, deliberately separate:
    //
    // * **Attributes only** (kSecReturnAttributes) for the modification date. It
    //   returns no secret material, so it is not the query the consent prompt
    //   guards, and a poll can use it every tick to notice a re-login without
    //   ever asking the user anything.
    // * **Data** (kSecReturnData) for the credential itself. This is the one that
    //   prompts, and it is called only when a stamp change or a deliberate action
    //   says it is worth asking.
    //
    // Nothing here logs, caches or stores what it reads. The JSON comes back as a
    // string handed straight to the caller; see the custody rules in
    // ClaudeCliCredentials for what that string may and may not be used for, and
    // for the honest note that it cannot be zeroed afterwards.
    [ExcludeFromCodeCoverage]
    internal static class MacOSKeychain
    {
        private const string Security =
            "/System/Library/Frameworks/Security.framework/Security";

        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // OSStatus values, from SecBase.h. Named rather than inlined because the
        // mapping below is the part a reader needs to check.
        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecAuthFailed = -25293;
        private const int ErrSecUserCanceled = -128;
        private const int ErrSecInteractionNotAllowed = -25308;

        [DllImport(Security)]
        private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys,
            IntPtr[] values, long count, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern long CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern double CFDateGetAbsoluteTime(IntPtr date);

        [DllImport(CoreFoundation)]
        private static extern long CFGetTypeID(IntPtr reference);

        [DllImport(CoreFoundation)]
        private static extern long CFDataGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern long CFDateGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr reference);

        private const uint KCFStringEncodingUtf8 = 0x08000100;

        // The framework constants are exported *variables* holding CFStringRefs,
        // so the symbol's address has to be dereferenced to get the string. The
        // dictionary callback tables are exported structs, where the address
        // itself is what CFDictionaryCreate wants — hence the two helpers rather
        // than one.
        private static IntPtr ConstantValue(string library, string symbol)
        {
            var handle = NativeLibrary.Load(library);
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, symbol));
        }

        private static IntPtr ConstantAddress(string library, string symbol)
        {
            var handle = NativeLibrary.Load(library);
            return NativeLibrary.GetExport(handle, symbol);
        }

        // Read the credential. **This is the call that prompts.**
        //
        // Returns the stored blob verbatim; parsing is ClaudeCliCredentials' job,
        // so the untestable half and the testable half stay apart.
        internal static (CredentialOutcome Outcome, string? Json, string? Detail)
            ReadGenericPassword(string service)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return (CredentialOutcome.Unreadable, null, "not macOS");
            }

            var query = IntPtr.Zero;
            var serviceString = IntPtr.Zero;
            var result = IntPtr.Zero;

            try
            {
                serviceString = CFStringCreateWithCString(IntPtr.Zero, service, KCFStringEncodingUtf8);
                if (serviceString == IntPtr.Zero)
                {
                    return (CredentialOutcome.Unreadable, null, "could not build the query");
                }

                query = BuildQuery(serviceString,
                    ConstantValue(Security, "kSecReturnData"));
                if (query == IntPtr.Zero)
                {
                    return (CredentialOutcome.Unreadable, null, "could not build the query");
                }

                var status = SecItemCopyMatching(query, out result);
                if (status != ErrSecSuccess)
                {
                    return (OutcomeForStatus(status), null, DetailForStatus(status));
                }

                if (result == IntPtr.Zero || CFGetTypeID(result) != CFDataGetTypeID())
                {
                    return (CredentialOutcome.Malformed, null,
                        "the Keychain item is not the kind of data expected");
                }

                var length = CFDataGetLength(result);
                var bytes = CFDataGetBytePtr(result);
                if (length <= 0 || bytes == IntPtr.Zero)
                {
                    return (CredentialOutcome.Malformed, null, "the Keychain item is empty");
                }

                var buffer = new byte[length];
                Marshal.Copy(bytes, buffer, 0, (int)length);
                return (CredentialOutcome.Found, Encoding.UTF8.GetString(buffer), null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // A future macOS that moved or renamed any of this. Degrading to
                // "we could not read it" is right; there are no cloud orbs and
                // nothing else in the app is affected.
                return (CredentialOutcome.Unreadable, null, "the Keychain API is unavailable");
            }
            finally
            {
                if (result != IntPtr.Zero) CFRelease(result);
                if (query != IntPtr.Zero) CFRelease(query);
                if (serviceString != IntPtr.Zero) CFRelease(serviceString);
            }
        }

        // The attributes-only query: a change detector that never returns secret
        // material and therefore never prompts.
        //
        // kSecAttrModificationDate is a CFDate in CoreFoundation's absolute time
        // base. The value is only ever compared with a previous one, so it is
        // rendered as the raw seconds rather than converted — there is nothing to
        // be gained by pretending it is a wall clock.
        internal static string? ModificationStamp(string service)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            var query = IntPtr.Zero;
            var serviceString = IntPtr.Zero;
            var result = IntPtr.Zero;

            try
            {
                serviceString = CFStringCreateWithCString(IntPtr.Zero, service, KCFStringEncodingUtf8);
                if (serviceString == IntPtr.Zero) return null;

                query = BuildQuery(serviceString,
                    ConstantValue(Security, "kSecReturnAttributes"));
                if (query == IntPtr.Zero) return null;

                if (SecItemCopyMatching(query, out result) != ErrSecSuccess) return null;
                if (result == IntPtr.Zero) return null;

                var modified = CFDictionaryGetValue(result,
                    ConstantValue(Security, "kSecAttrModificationDate"));
                if (modified == IntPtr.Zero || CFGetTypeID(modified) != CFDateGetTypeID()) return null;

                return CFDateGetAbsoluteTime(modified)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (result != IntPtr.Zero) CFRelease(result);
                if (query != IntPtr.Zero) CFRelease(query);
                if (serviceString != IntPtr.Zero) CFRelease(serviceString);
            }
        }

        // { kSecClass: kSecClassGenericPassword, kSecAttrService: <service>,
        //   kSecMatchLimit: kSecMatchLimitOne, <returnKey>: true }
        //
        // kSecAttrAccount is deliberately not constrained. The CLI writes one item
        // under this service and the account name it uses is not something we
        // measured, so pinning it would be an assumption that fails silently with
        // ItemNotFound the day it is wrong.
        private static IntPtr BuildQuery(IntPtr serviceString, IntPtr returnKey)
        {
            var keys = new[]
            {
                ConstantValue(Security, "kSecClass"),
                ConstantValue(Security, "kSecAttrService"),
                ConstantValue(Security, "kSecMatchLimit"),
                returnKey,
            };

            var values = new[]
            {
                ConstantValue(Security, "kSecClassGenericPassword"),
                serviceString,
                ConstantValue(Security, "kSecMatchLimitOne"),
                ConstantValue(CoreFoundation, "kCFBooleanTrue"),
            };

            foreach (var pointer in keys)
            {
                if (pointer == IntPtr.Zero) return IntPtr.Zero;
            }

            return CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length,
                ConstantAddress(CoreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                ConstantAddress(CoreFoundation, "kCFTypeDictionaryValueCallBacks"));
        }

        // The mapping CB-164 specified. Denied covers both halves of "the user
        // said no": an explicit cancel, and an authorisation failure — plus the
        // case where macOS wanted to prompt and could not, which is what a
        // background or pre-login context produces and which must not be retried
        // into a prompt storm.
        private static CredentialOutcome OutcomeForStatus(int status) => status switch
        {
            ErrSecItemNotFound => CredentialOutcome.NotLoggedIn,
            ErrSecUserCanceled => CredentialOutcome.Denied,
            ErrSecAuthFailed => CredentialOutcome.Denied,
            ErrSecInteractionNotAllowed => CredentialOutcome.Denied,
            _ => CredentialOutcome.Unreadable,
        };

        private static string DetailForStatus(int status) => status switch
        {
            ErrSecItemNotFound => "no Claude Code login is stored in the Keychain",
            ErrSecUserCanceled => "the Keychain prompt was declined",
            ErrSecAuthFailed => "the Keychain refused access",
            ErrSecInteractionNotAllowed => "the Keychain could not prompt in this context",
            _ => $"the Keychain returned OSStatus {status}",
        };
    }
}
