using Microsoft.Extensions.Logging;

namespace Translation.Gateway.Logging;

public sealed class UiLoggerProvider : ILoggerProvider
{
    private readonly UiLogStore _store;

    public UiLoggerProvider(UiLogStore store) => _store = store;

    public ILogger CreateLogger(string categoryName) => new UiLogger(_store, categoryName);

    public void Dispose() { }

    private sealed class UiLogger : ILogger
    {
        private readonly UiLogStore _store;
        private readonly string _cat;

        public UiLogger(UiLogStore store, string category)
        {
            _store = store;
            _cat = category;
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var msg = formatter(state, exception);
            if (exception != null) msg += Environment.NewLine + exception;

            _store.Add(logLevel.ToString(), _cat, msg);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}