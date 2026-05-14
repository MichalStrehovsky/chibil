namespace Chibicc;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            new Driver().Run(args);
            return 0;
        }
        catch (ChibiccException ex)
        {
            Console.Error.Write(ex.Message);
            return 1;
        }
    }
}
