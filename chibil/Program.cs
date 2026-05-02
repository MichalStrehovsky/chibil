namespace Chibil;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            new Driver().Run(args);
            return 0;
        }
        catch (ChibiException ex)
        {
            Console.Error.Write(ex.Message);
            return 1;
        }
    }
}
