namespace Chilink;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            new Driver().Run(args);
            return 0;
        }
        catch (ChilinkException ex)
        {
            Console.Error.WriteLine($"chilink: {ex.Message}");
            return 1;
        }
    }
}
