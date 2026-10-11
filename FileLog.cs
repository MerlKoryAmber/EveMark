namespace JitaWatch;

internal sealed class FileLogProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileLogProvider(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _path, _gate);

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _path;
        private readonly object _gate;

        public FileLogger(string category, string path, object gate)
        {
            _category = category;
            _path = path;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logLevel}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            lock (_gate)
            {
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > 2_000_000)
                {
                    var tail = Math.Min(file.Length, 512_000);
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    stream.Seek(-tail, SeekOrigin.End);
                    var buf = new byte[tail];
                    stream.ReadExactly(buf);
                    stream.SetLength(0);
                    stream.Write(buf);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
    }
}
