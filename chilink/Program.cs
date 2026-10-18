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
        catch (LinkException ex)
        {
            Console.Error.WriteLine($"chilink: error: {ex.Message}");
            return 1;
        }
    }
}
