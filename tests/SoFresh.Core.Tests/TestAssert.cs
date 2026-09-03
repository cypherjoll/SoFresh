namespace SoFresh.Core.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new TestFailureException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }
}

internal sealed class TestFailureException(string message) : Exception(message);

internal sealed class TestSkippedException(string message) : Exception(message);
