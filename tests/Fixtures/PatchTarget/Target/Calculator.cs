namespace Tesseris.Tests.PatchTarget;

public static class Calculator
{
    public static int Compute(int value) => value * 2;

    public static int MultipleReturns(int value)
    {
        if (value < 0) return -1;
        return value + 1;
    }
}
