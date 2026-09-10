using Xunit;

namespace ClaudeBuddy.Tests;

// CLAUDE_BUDDY_LOG_DIR is one process-wide environment variable, and two
// classes here point it at a scratch directory of their own: the crash log's
// and the persona rejection log's. Run in parallel they would each be writing
// into the other's directory half the time — and the failure would not be a
// clean one, because a log write that lands somewhere unexpected is silent by
// design in both of them.
//
// Same argument, and same shape, as [Collection("Settings")] in tests/UiTests.
[CollectionDefinition("LogDir")]
public class LogDirCollection { }
