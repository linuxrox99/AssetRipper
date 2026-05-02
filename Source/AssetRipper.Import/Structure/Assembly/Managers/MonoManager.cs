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
								if (assemblyName.Equals("UnityEngine.dll", StringComparison.Ordinal))
									{
										LogUnityEngineDiagnostics(assemblyPath, gameStructure.FileSystem, ex);
									}
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

	private static void LogUnityEngineDiagnostics(string assemblyPath, FileSystem fileSystem, Exception rootException)
	{
		try
		{
			using Stream stream = fileSystem.File.OpenRead(assemblyPath);
			using MemoryStream ms = new();
			stream.CopyTo(ms);
			byte[] data = ms.ToArray();

			int markerCount = 0;
			markerCount += CountUtf8(data, "255.255");
			markerCount += CountUtf8(data, "255.255.255.255");
			markerCount += CountUtf8(data, "WindowsPhone,Version=v8.0");
			markerCount += CountUtf16Le(data, "255.255");
			markerCount += CountUtf16Le(data, "255.255.255.255");
			markerCount += CountUtf16Le(data, "WindowsPhone,Version=v8.0");

			markerCount += CountUtf8(data, "WindowsRuntime 255.255");
			markerCount += CountUtf16Le(data, "WindowsRuntime 255.255");
			markerCount += CountUtf8(data, ".NETCoreApp,Version=v255.255");
			markerCount += CountUtf16Le(data, ".NETCoreApp,Version=v255.255");

			Logger.Warning(LogCategory.Import, $"UnityEngine diagnostics: size={data.Length} bytes, markerHits={markerCount}, parseError={rootException.Message}");
			LogMarkerPreview(data, "255.255");
			LogMarkerPreview(data, "WindowsRuntime");
			LogMarkerPreview(data, ".NETCoreApp,Version=");

		}
		catch (Exception diagException)
		{
			Logger.Warning(LogCategory.Import, $"UnityEngine diagnostics failed: {diagException.Message}");
		}
	}

	private static void LogMarkerPreview(byte[] data, string marker)
	{
		byte[] markerBytes = Encoding.UTF8.GetBytes(marker);
		for (int i = 0; i <= data.Length - markerBytes.Length; i++)
		{
			if (!data.AsSpan(i, markerBytes.Length).SequenceEqual(markerBytes))
			{
				continue;
			}
			int start = Math.Max(0, i - 24);
			int len = Math.Min(96, data.Length - start);
			string preview = Encoding.UTF8.GetString(data, start, len).Replace("\0", "\\0");
			Logger.Warning(LogCategory.Import, $"UnityEngine marker preview [{marker}]: {preview}");
			return;
		}
	}

	private static int CountUtf8(byte[] buffer, string value) => CountPattern(buffer, Encoding.UTF8.GetBytes(value));

	private static int CountUtf16Le(byte[] buffer, string value) => CountPattern(buffer, Encoding.Unicode.GetBytes(value));

	private static int CountPattern(byte[] buffer, byte[] pattern)
	{
		int count = 0;
		for (int i = 0; i <= buffer.Length - pattern.Length; i++)
		{
			if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
			{
				count++;
				i += pattern.Length - 1;
			}
		}
		return count;
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
			| ReplaceUtf8(data, "255.255", "4.0.303")
			| ReplaceUtf16Le(data, "v255.255", "v4.0.303")
			| ReplaceUtf16Le(data, "255.255", "4.0.303")
			| ReplaceWithPaddingUtf8(data, "255.255.255.255", "4.0.0.0")
			| ReplaceWithPaddingUtf16Le(data, "255.255.255.255", "4.0.0.0")
			| ReplaceWithPaddingUtf8(data, "WindowsRuntime 255.255", "v4.0.30319")
			| ReplaceWithPaddingUtf16Le(data, "WindowsRuntime 255.255", "v4.0.30319")
			| ReplaceWithPaddingUtf8(data, ".NETCoreApp,Version=v255.255", ".NETFramework,Version=4.0")
			| ReplaceWithPaddingUtf16Le(data, ".NETCoreApp,Version=v255.255", ".NETFramework,Version=4.0");

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


	private static bool ReplaceUtf16Le(byte[] buffer, string oldValue, string newValue)
	{
		byte[] oldBytes = Encoding.Unicode.GetBytes(oldValue);
		byte[] newBytes = Encoding.Unicode.GetBytes(newValue);
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


	private static bool ReplaceWithPaddingUtf8(byte[] buffer, string oldValue, string newValue)
	{
		return ReplaceWithPadding(buffer, Encoding.UTF8.GetBytes(oldValue), Encoding.UTF8.GetBytes(newValue));
	}

	private static bool ReplaceWithPaddingUtf16Le(byte[] buffer, string oldValue, string newValue)
	{
		return ReplaceWithPadding(buffer, Encoding.Unicode.GetBytes(oldValue), Encoding.Unicode.GetBytes(newValue));
	}

	private static bool ReplaceWithPadding(byte[] buffer, byte[] oldBytes, byte[] newBytes)
	{
		if (newBytes.Length > oldBytes.Length)
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
			buffer.AsSpan(i + newBytes.Length, oldBytes.Length - newBytes.Length).Clear();
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
