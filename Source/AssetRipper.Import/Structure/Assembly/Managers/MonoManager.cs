using AsmResolver.PE.File;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using System.Text;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public sealed class MonoManager : BaseManager
{
	public const string AssemblyExtension = ".dll";

	public override ScriptingBackend ScriptingBackend => ScriptingBackend.Mono;

	public MonoManager(Action<string> requestAssemblyCallback) : base(requestAssemblyCallback) { }

	public override void Initialize(PlatformGameStructure gameStructure)
	{
		Logger.Info(LogCategory.Import, $"During Mono initialization, found {gameStructure.Assemblies.Count} assemblies");
		foreach ((string assemblyName, string assemblyPath) in gameStructure.Assemblies)
		{
			try
			{
				using Stream stream = gameStructure.FileSystem.File.OpenRead(assemblyPath);
				PEFile peFile = PEFile.FromStream(stream);
				if (!peFile.OptionalHeader.GetDataDirectory(DataDirectoryIndex.ClrDirectory).IsPresentInPE)
				{
					Logger.Info(LogCategory.Import, $"Skipping native assembly: {assemblyName}");
				}
				else
				{
					try
					{
						Load(assemblyPath, gameStructure.FileSystem);
					}
					catch (ArgumentException ex)
					{
						if (TryLoadWithWindowsPhoneMetadataCompatibility(assemblyName, assemblyPath, gameStructure.FileSystem, out string? compatibilityMessage))
						{
							Logger.Warning(LogCategory.Import, $"Loaded managed assembly '{assemblyName}' using Windows Phone metadata compatibility mode. {compatibilityMessage}");
						}
						else
						{
							Logger.Warning(LogCategory.Import, $"Skipping unsupported managed assembly '{assemblyName}': {ex.Message}");
						}
					}
				}
			}
			catch (BadImageFormatException)
			{
				Logger.Info(LogCategory.Import, $"Skipping non-PE file: {assemblyName}");
			}
		}
	}

	private bool TryLoadWithWindowsPhoneMetadataCompatibility(string assemblyName, string assemblyPath, FileSystem fileSystem, [NotNullWhen(true)] out string? message)
	{
		byte[] data;
		using (Stream stream = fileSystem.File.OpenRead(assemblyPath))
		using (MemoryStream ms = new())
		{
			stream.CopyTo(ms);
			data = ms.ToArray();
		}

		bool patchedFramework = ReplaceUtf8(data, "WindowsPhone,Version=v8.0", ".NETFramework,Version=4.0");
		bool patchedVersion255 = ReplaceUtf8(data, "v255.255", "v4.0.303")
			| ReplaceUtf8(data, "255.255", "4.0.303");

		if (!patchedFramework && !patchedVersion255)
		{
			message = null;
			return false;
		}

		try
		{
			Read(new MemoryStream(data, writable: false), assemblyName);
			message = $"Patched metadata markers: framework={patchedFramework}, runtime={patchedVersion255}";
			return true;
		}
		catch (Exception ex)
		{
			message = $"Compatibility parse failed: {ex.Message}";
			return false;
		}
	}

	private static bool ReplaceUtf8(byte[] buffer, string oldValue, string newValue)
	{
		byte[] oldBytes = Encoding.UTF8.GetBytes(oldValue);
		byte[] newBytes = Encoding.UTF8.GetBytes(newValue);
		if (oldBytes.Length != newBytes.Length)
		{
			return false;
		}

		bool replaced = false;
		for (int i = 0; i <= buffer.Length - oldBytes.Length; i++)
		{
			if (!buffer.AsSpan(i, oldBytes.Length).SequenceEqual(oldBytes))
			{
				continue;
			}

			newBytes.CopyTo(buffer, i);
			replaced = true;
			i += oldBytes.Length - 1;
		}
		return replaced;
	}

	public static bool IsMonoAssembly(string fileName)
	{
		if (fileName.EndsWith(AssemblyExtension, StringComparison.Ordinal))
		{
			return true;
		}
		return false;
	}
}
