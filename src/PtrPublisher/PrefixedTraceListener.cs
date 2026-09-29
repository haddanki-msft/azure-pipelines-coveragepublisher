using System.Diagnostics;

namespace PtrPublisher;

/// <summary>
/// Sends Ta library traces to stderr with a prefix so they cannot be interpreted as GitHub workflow
/// commands, and redacts the access token if it ever appears in a message.
/// </summary>
internal sealed class PrefixedTraceListener : TraceListener
{
    private readonly string? secret;
    private readonly object gate = new();
    private bool atLineStart = true;

    public PrefixedTraceListener(string? secret)
    {
        this.secret = string.IsNullOrEmpty(secret) ? null : secret;
    }

    public override void Write(string? message) => Emit(message, newLine: false);

    public override void WriteLine(string? message) => Emit(message, newLine: true);

    private void Emit(string? message, bool newLine)
    {
        message ??= string.Empty;
        if (secret is not null)
        {
            message = message.Replace(secret, "***", StringComparison.Ordinal);
        }

        lock (gate)
        {
            var lines = message.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (atLineStart)
                {
                    Console.Error.Write("[ptr] ");
                }
                Console.Error.Write(lines[i]);
                var isLast = i == lines.Length - 1;
                if (!isLast || newLine)
                {
                    Console.Error.WriteLine();
                    atLineStart = true;
                }
                else
                {
                    atLineStart = lines[i].Length == 0 && atLineStart;
                }
            }
        }
    }
}
