using Engine.Core;

if (args is not ["material", "create", var destination])
{
    Console.Error.WriteLine("Usage: Engine.AssetTool material create <destination.nmat>");
    return 2;
}

var fullPath = Path.GetFullPath(destination);
var directory = Path.GetDirectoryName(fullPath)
    ?? throw new InvalidOperationException("Destination has no parent directory.");
Directory.CreateDirectory(directory);
var temporaryPath = fullPath + ".tmp";
try
{
    using (var stream = new FileStream(
               temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        StandardMaterialAssetCodec.Save(stream, new StandardMaterialAsset());
    File.Move(temporaryPath, fullPath, overwrite: true);
}
finally
{
    if (File.Exists(temporaryPath))
        File.Delete(temporaryPath);
}

Console.WriteLine(fullPath);
return 0;
