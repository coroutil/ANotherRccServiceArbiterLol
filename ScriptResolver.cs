namespace Arbiter;

public static class ScriptResolver
{
    public static string GetScript(string type)
    {
        var path = type.ToLowerInvariant() switch
        {
            "gameserver" => Configuration.GetStringFlag("DFStringGameServerScriptPath"),
            "placerender" => Configuration.GetStringFlag("DFStringPlaceRenderScriptPath"),
            "avatarrender" => Configuration.GetStringFlag("DFStringAvatarRenderScriptPath"),
            "modelrender" => Configuration.GetStringFlag("DFStringModelRenderScriptPath"),
            "meshrender" => Configuration.GetStringFlag("DFStringMeshRenderScriptPath"),
            "clothingrender" => Configuration.GetStringFlag("DFStringClothingRenderScriptPath"),
            "packagerender" => Configuration.GetStringFlag("DFStringPackageRenderScriptPath"),

            _ => throw new Exception($"Unknown job type: {type}")
        };

        Console.WriteLine($"Length: {type?.Length}");

        if (string.IsNullOrWhiteSpace(path))
            throw new Exception($"No script path configured for job type '{type}'");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Script file not found: {path}");

        return File.ReadAllText(path);
    }
}