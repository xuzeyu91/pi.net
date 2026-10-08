// Pi.Tui.Tests shares process-global state with the code under test (terminal capability cache,
// Kitty metadata registry, environment-variable detection, the global keybinding registry and the
// native platform helper). xUnit runs test collections in parallel by default, which would make those
// tests interfere with each other, so parallelisation is disabled for the whole assembly.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
