namespace Lingofix.Backend.Documents;

public interface IRunLogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Progress(int percent, string message);
}

public sealed class NullRunLogger : IRunLogger
{
    public static readonly NullRunLogger Instance = new();

    private NullRunLogger()
    {
    }

    public void Info(string message)
    {
    }

    public void Warning(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Progress(int percent, string message)
    {
    }
}
