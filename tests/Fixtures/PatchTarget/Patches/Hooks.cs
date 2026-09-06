namespace Tesseris.Tests.PatchMethods;

public static class Hooks
{
    public static string Trace { get; private set; } = string.Empty;

    public static void Reset() => Trace = string.Empty;

    public static void PrefixA(int value) => Trace += "A";

    public static void PrefixB(int value) => Trace += "B";

    public static int PostfixA(int value, int result)
    {
        Trace += "a";
        return result + 1;
    }

    public static int PostfixB(int value, int result)
    {
        Trace += "b";
        return result + 10;
    }

    public static int Replace(int value)
    {
        Trace += "R";
        return value + 100;
    }

    public static int ReplaceConflict(int value)
    {
        Trace += "X";
        return value - 100;
    }

    public static string WrongSignature(string value) => value;
}
