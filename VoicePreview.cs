using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The settings window's "hear this voice" button (CB-222): one fixed sentence,
    // spoken in the voice the picker shows, through the same TextToSpeech channel
    // every other speaker button uses. There is still only one voice talking in the
    // process, so this class does not own a speech engine of its own. What it owns
    // is *whether a preview is wanted*, and the bookkeeping that tells "my preview"
    // apart from "somebody else's read-aloud" — which is the whole difficulty,
    // because TextToSpeech's state is one static shared by every orb.
    //
    // Two rules, both bought by the notification-sound preview's QA rounds and
    // repeated here rather than rediscovered:
    //
    //   * Nothing that can block runs on the UI thread. Speak() starts a process
    //     and cancels whatever was speaking first; Cancel() waits up to three
    //     seconds on taskkill; resolving the saved voice can run `say -v ?`. All
    //     of it goes down one background chain.
    //   * Two previews never overlap, however fast the button is clicked. The
    //     chain is serial, so click order is execution order, and every queued
    //     start carries the request id it was made under: a start whose id has
    //     moved on by the time it runs never calls Speak at all.
    //
    // How "mine" is told from "theirs": every TextToSpeech.Speak begins with
    // Cancel(), which bumps StopGeneration (see the comment on it). The preview
    // records the generation it is speaking under once Speak returns; if the
    // counter later differs, something else has spoken since and this preview is
    // over, whatever the shared state says. That is also why stopping only cancels
    // when the counter still matches — a preview that has been superseded must not
    // kill the read-aloud that superseded it.
    //
    // Known and accepted: the generation is read after Speak returns, so a
    // read-aloud that starts in the microseconds between Speak's own Cancel() and
    // that read would be adopted as the preview's. Closing it means Speak has to
    // report the generation it started under, which touches an excluded method on
    // every speak path for a race that needs two speech requests in the same
    // instant. Not worth it for a settings button.
    internal static class VoicePreview
    {
        // A sentence that says what the feature is for, and not the voice's name:
        // Kokoro ids like af_heart and the custom command's empty name read badly
        // aloud.
        internal const string SampleText =
            $"Hello. This is how {Brand.DisplayName} will sound when it reads a reply aloud.";

        private static readonly object Gate = new();

        // Bumped on every start and every stop. A queued start compares against it.
        private static int _requestId;

        // A preview is wanted or live. Cleared when it stops, ends by itself, or is
        // superseded.
        private static bool _requested;

        // Null while the start is queued or running and Speak has not yet returned.
        private static int? _ownedGeneration;

        private static Task _chain = Task.CompletedTask;
        private static bool _subscribed;

        // Always raised on the UI thread; the button subscribes while attached.
        internal static event Action? Changed;

        // ---- the pure decisions -------------------------------------------------

        // What the button should show. Not simply the global state: a preview that
        // was superseded must not mirror somebody else's speech, and one that has
        // been asked for but not started is "Preparing" so the click answers
        // instantly rather than after Kokoro's three seconds.
        internal static TextToSpeech.SpeakState LookFor(
            bool requested, int? ownedGeneration, int currentGeneration,
            TextToSpeech.SpeakState global)
        {
            if (!requested) return TextToSpeech.SpeakState.Idle;
            if (ownedGeneration is null) return TextToSpeech.SpeakState.Preparing;
            if (ownedGeneration != currentGeneration) return TextToSpeech.SpeakState.Idle;
            return global;
        }

        // A started preview that now looks Idle has finished or been superseded, so
        // the request can be cleared. A queued one never counts: Speak's own
        // Cancel() reports Idle before its engine starts, and reading that as "the
        // preview ended" would drop it before it began.
        internal static bool Ended(
            bool requested, int? ownedGeneration, int currentGeneration,
            TextToSpeech.SpeakState global) =>
            requested && ownedGeneration is not null
            && LookFor(requested, ownedGeneration, currentGeneration, global)
                == TextToSpeech.SpeakState.Idle;

        // A queued start whose request has been overtaken by a stop or a newer
        // start must not run, which is what makes "click, click" start nothing.
        internal static bool ShouldStartNow(int requestIdAtEnqueue, int currentRequestId) =>
            requestIdAtEnqueue == currentRequestId;

        // Stopping only cancels speech this preview started. Anything else — a
        // read-aloud that took over, or a preview that never got going — is left
        // alone.
        internal static bool ShouldCancel(int? ownedGeneration, int currentGeneration) =>
            ownedGeneration is not null && ownedGeneration == currentGeneration;

        // The voice the picker is showing, or null when it is still the unscanned
        // placeholder (or nothing valid is selected), meaning "resolve the saved
        // selection in the worker" exactly as a real read-aloud would.
        internal static TextToSpeech.VoiceOption? PreviewTarget(
            List<TextToSpeech.VoiceOption>? options, int selectedIndex) =>
            options is not null && selectedIndex >= 0 && selectedIndex < options.Count
                ? options[selectedIndex]
                : null;

        // ---- state visible to the button ----------------------------------------

        internal static TextToSpeech.SpeakState Look
        {
            get
            {
                lock (Gate)
                {
                    return LookFor(_requested, _ownedGeneration,
                        TextToSpeech.StopGeneration, TextToSpeech.State);
                }
            }
        }

        // ---- operations (UI thread; each returns at once) ------------------------

        // The button's click. Stops when a preview is live or queued, starts one
        // otherwise. The returned task completes when the work this click queued
        // has run, so tests can await it; production ignores it.
        internal static Task Toggle(TextToSpeech.VoiceOption? target) =>
            Look != TextToSpeech.SpeakState.Idle ? StopIfLive() : Start(target);

        private static Task Start(TextToSpeech.VoiceOption? target)
        {
            int id;
            Task task;
            lock (Gate)
            {
                EnsureSubscribed();
                id = ++_requestId;
                _requested = true;
                _ownedGeneration = null;
                task = Enqueue(() => RunStart(id, target));
            }

            RaiseChanged();
            return task;
        }

        // For the button, window close and selection change. Idempotent, and cheap
        // when nothing is live: it bumps the request id (so a queued start is
        // skipped) and queues a cancel only if there is speech to cancel.
        internal static Task StopIfLive()
        {
            int? owned;
            Task task;
            lock (Gate)
            {
                _requestId++;
                owned = _ownedGeneration;
                _requested = false;
                _ownedGeneration = null;
                task = Enqueue(() =>
                {
                    try
                    {
                        if (ShouldCancel(owned, TextToSpeech.StopGeneration)) DoCancel();
                    }
                    catch (Exception ex)
                    {
                        // Enqueue relies on its work catching its own exceptions, and
                        // a Cancel() that throws would otherwise fault the chain and
                        // hand the caller's await an exception nobody observes.
                        Console.Error.WriteLine($"{Brand.DisplayName}: voice preview stop failed: {ex.Message}");
                    }
                });
            }

            RaiseChanged();
            return task;
        }

        // ---- the worker ----------------------------------------------------------

        private static void RunStart(int id, TextToSpeech.VoiceOption? target)
        {
            try
            {
                lock (Gate)
                {
                    if (!ShouldStartNow(id, _requestId)) return;
                }

                var voice = target ?? DoResolveSaved();
                if (voice is null)
                {
                    // Nothing on this machine can speak. Ends the request quietly;
                    // the button going back to Idle is the whole report.
                    End(id);
                    return;
                }

                DoSpeak(SampleText, voice);

                var generation = TextToSpeech.StopGeneration;
                var stoppedMeanwhile = false;
                lock (Gate)
                {
                    if (id == _requestId && _requested)
                    {
                        _ownedGeneration = generation;
                    }
                    else
                    {
                        // A stop landed while Speak was running. Its own queued
                        // cancel captured no generation (there was none yet), so
                        // the speech that has just started is ours to stop. A
                        // *newer start* is different: its Speak cancels this one.
                        stoppedMeanwhile = !_requested;
                    }
                }

                if (stoppedMeanwhile && ShouldCancel(generation, TextToSpeech.StopGeneration))
                {
                    DoCancel();
                }

                // The engine may already have come and gone (a custom command that
                // fails at once), so look again now rather than wait for an event
                // that has already been and gone.
                Refresh();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Brand.DisplayName}: voice preview failed: {ex.Message}");
                End(id);
            }
        }

        private static void End(int id)
        {
            lock (Gate)
            {
                if (id != _requestId) return;
                _requested = false;
                _ownedGeneration = null;
            }

            RaiseChanged();
        }

        // Runs on whatever thread TextToSpeech reported from. Recomputes the look,
        // retires a preview that has ended, and tells the button.
        private static void Refresh()
        {
            bool relevant;
            lock (Gate)
            {
                // Every speech event in the process arrives here once anything has
                // been previewed; only tell the button when a preview was in play.
                relevant = _requested;
                if (Ended(_requested, _ownedGeneration,
                        TextToSpeech.StopGeneration, TextToSpeech.State))
                {
                    _requested = false;
                    _ownedGeneration = null;
                }
            }

            if (relevant) RaiseChanged();
        }

        // Called with Gate held. Chained rather than Task.Run'd so that order is
        // preserved; the work catches its own exceptions, so the chain never faults.
        private static Task Enqueue(Action work)
        {
            _chain = _chain.ContinueWith(_ => work(), TaskScheduler.Default);
            return _chain;
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            TextToSpeech.StateChanged += _ => Refresh();
        }

        // ---- UI-thread marshalling -----------------------------------------------

        // Posts rather than invokes when called from a worker, the same shape as
        // RemoteControlChatSession. Handlers touch Avalonia, and StateChanged fires
        // on whichever thread an Exited handler happens to run on.
        internal static Action<Action>? UiPostForTests;

        private static void RaiseChanged()
        {
            void Raise() => Changed?.Invoke();

            if (UiPostForTests is not null) UiPostForTests(Raise);
            else if (Dispatcher.UIThread.CheckAccess()) Raise();
            else Dispatcher.UIThread.Post(Raise);
        }

        // The chain as it stands, so a test can wait for everything queued so far.
        internal static Task Drained()
        {
            lock (Gate) return _chain;
        }

        // ---- seams ---------------------------------------------------------------

        internal static Action<string, TextToSpeech.VoiceOption>? SpeakForTests;
        internal static Action? CancelForTests;
        internal static Func<TextToSpeech.VoiceOption?>? ResolveSavedForTests;

        // Clears everything, including the chain and the seams, for the same reason
        // SettingsWindow.ResetPreviewDebounceForTests exists: this is process-wide
        // state and a test that leaves it live changes the next one's answer.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _requestId++;
                _requested = false;
                _ownedGeneration = null;
                _chain = Task.CompletedTask;
            }

            SpeakForTests = null;
            CancelForTests = null;
            ResolveSavedForTests = null;
            UiPostForTests = null;
        }

        private static void DoSpeak(string text, TextToSpeech.VoiceOption voice)
        {
            if (SpeakForTests is { } fake) fake(text, voice);
            else RealSpeak(text, voice);
        }

        private static void DoCancel()
        {
            if (CancelForTests is { } fake) fake();
            else RealCancel();
        }

        private static TextToSpeech.VoiceOption? DoResolveSaved() =>
            ResolveSavedForTests is { } fake ? fake() : RealResolveSaved();

        // Excluded from coverage: starts a speech engine and makes the machine
        // make a noise.
        [ExcludeFromCodeCoverage]
        private static void RealSpeak(string text, TextToSpeech.VoiceOption voice) =>
            TextToSpeech.Speak(text, voice);

        // Excluded from coverage: kills a real speech process tree.
        [ExcludeFromCodeCoverage]
        private static void RealCancel() => TextToSpeech.Cancel();

        // Excluded from coverage: SelectedVoice() enumerates the machine's voices,
        // which means running `say -v ?` or asking the neural engine.
        [ExcludeFromCodeCoverage]
        private static TextToSpeech.VoiceOption? RealResolveSaved() =>
            TextToSpeech.SelectedVoice();
    }
}
