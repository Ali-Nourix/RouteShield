namespace RouteShield;

/// <summary>Best-effort removal of plaintext engine inputs, including a failed preflight.</summary>
public static class TransientConfiguration
{
    public static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(LogCategory.Config, $"Could not remove temporary configuration {Path.GetFileName(path)}: {exception.GetType().Name}");
            return false;
        }
    }
}
