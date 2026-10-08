using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Services;

/// <summary>
/// A folder of the application that files are copied into rather than read where the user keeps
/// them (see <see cref="OverlayLibrary"/> for why), named after their content as well as after
/// their name: the same file added twice is one file, and a name is never reused for another one,
/// so a browser may keep what it was given under that name.
/// <para>A file is written beside its final name and renamed onto it once complete and checked, so
/// the folder never lists a file that is half written or that the check refused. Only names the
/// folder gave out are ever answered: nothing outside it can be reached through one.</para>
/// </summary>
public sealed partial class MediaStore
{
    private const int HashLength = 10;

    /// <summary>The prefix of a file on its way in: never a name of the folder.</summary>
    private const string UploadPrefix = ".upload-";

    private readonly IReadOnlyDictionary<string, string> _contentTypes;
    private readonly string _fallbackStem;
    private readonly ILogger _logger;

    /// <param name="directory">The folder, created on the first file.</param>
    /// <param name="contentTypes">The extensions it takes (with the dot), and the type each is served as.</param>
    /// <param name="maxBytes">The largest file it takes.</param>
    /// <param name="fallbackStem">The name a file whose name has nothing readable in it is given.</param>
    public MediaStore(
        string directory,
        IReadOnlyDictionary<string, string> contentTypes,
        long maxBytes,
        string fallbackStem,
        ILogger logger)
    {
        Directory = Path.GetFullPath(directory);
        _contentTypes = contentTypes;
        MaxBytes = maxBytes;
        _fallbackStem = fallbackStem;
        _logger = logger;
    }

    public string Directory { get; }

    public long MaxBytes { get; }

    /// <summary>Whether a file of that name can join the folder at all, judged on its extension.</summary>
    public bool Accepts(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && _contentTypes.ContainsKey(Path.GetExtension(fileName));

    /// <summary>The type a file of the folder is served as.</summary>
    public string ContentTypeOf(string name) => _contentTypes[Path.GetExtension(name)];

    /// <summary>The files of the folder, the newest first: the one just added is the one about to be used.</summary>
    public IEnumerable<FileInfo> Files() =>
        System.IO.Directory.Exists(Directory)
            ? new DirectoryInfo(Directory).EnumerateFiles()
                .Where(file => IsStoreName(file.Name))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
            : [];

    /// <summary>A file of the folder, by its name: null for any name the folder did not give out, or that is gone.</summary>
    public string? PathOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || !IsStoreName(name))
        {
            return null;
        }

        var path = Path.Combine(Directory, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Whether a path is a file of the folder, written the way a layout or a row stores it.</summary>
    public bool Contains(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var full = Path.GetFullPath(StreamingService.NormalizeUserPath(path));
        var folder = Path.GetDirectoryName(full);
        return folder is not null
            && string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), Directory.TrimEnd(Path.DirectorySeparatorChar), PathComparison)
            && PathOf(Path.GetFileName(full)) is not null;
    }

    /// <summary>
    /// Copies a file into the folder and answers with its name there. The caller has checked the
    /// extension; <paramref name="check"/> looks at the copy before it is given a name, and throws
    /// to refuse it. A copy that grows past <see cref="MaxBytes"/> is refused with what
    /// <paramref name="tooLarge"/> answers, before the rest of it is read.
    /// </summary>
    public async Task<string> AddAsync(
        string label,
        System.IO.Stream content,
        Func<string, CancellationToken, Task> check,
        Func<Exception> tooLarge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(tooLarge);

        var extension = Path.GetExtension(label).ToLowerInvariant();
        System.IO.Directory.CreateDirectory(Directory);
        var upload = Path.Combine(Directory, $"{UploadPrefix}{Guid.NewGuid():N}{extension}");

        try
        {
            string hash;
            await using (var file = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw tooLarge();
                    }

                    digest.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                hash = Convert.ToHexStringLower(digest.GetHashAndReset())[..HashLength];
            }

            await check(upload, cancellationToken).ConfigureAwait(false);

            var name = $"{StemOf(label, _fallbackStem)}-{hash}{extension}";
            var path = Path.Combine(Directory, name);
            try
            {
                File.Move(upload, path);
                _logger.LogInformation("{Label} added to {Folder} as {Name}", label, Path.GetFileName(Directory), name);
            }
            catch (IOException) when (File.Exists(path))
            {
                // The same file is already there, added before or at this same moment: the name is
                // the content, so the one on disk is this one. It is as new as this copy, for the
                // sweep: whoever added it again is about to use it.
                TryTouch(path);
            }

            return name;
        }
        finally
        {
            if (File.Exists(upload))
            {
                File.Delete(upload);
            }
        }
    }

    /// <summary>
    /// Deletes a file of the folder. One that cannot go now - a process is reading it, as an ffmpeg
    /// that has it on air does on Windows - stays, and false says so.
    /// </summary>
    public bool TryDelete(string name)
    {
        if (PathOf(name) is not { } path)
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(exception, "{Name} is in use and stays in {Folder} for now", name, Path.GetFileName(Directory));
            return false;
        }
    }

    /// <summary>
    /// Deletes what nothing is going to use: the files older than <paramref name="age"/> that
    /// <paramref name="keep"/> does not claim, and the uploads a crash left half written. The age
    /// is what spares a file just added for a form that has not been saved yet.
    /// </summary>
    public void Sweep(TimeSpan age, Func<string, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        var before = DateTime.UtcNow - age;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles())
        {
            if (file.LastWriteTimeUtc > before)
            {
                continue;
            }

            if (file.Name.StartsWith(UploadPrefix, StringComparison.Ordinal))
            {
                TryDeleteFile(file);
            }
            else if (IsStoreName(file.Name) && !keep(file.Name))
            {
                TryDeleteFile(file);
                _logger.LogInformation("{Name} left {Folder}: nothing uses it", file.Name, Path.GetFileName(Directory));
            }
        }
    }

    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Read by a live right now: it is plainly in use, which is what the time was to say.
        }
    }

    private static void TryDeleteFile(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In use right now: the next sweep tries again.
        }
    }

    /// <summary>The name the file was added with: the stored name less the hash of its content.</summary>
    public static string LabelOf(string name)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var match = HashSuffix().Match(stem);
        return (match.Success ? stem[..match.Index] : stem) + extension;
    }

    /// <summary>
    /// The readable part of the stored name: letters, digits and a few separators, nothing a file
    /// system or a URL could read as something else, and never empty.
    /// </summary>
    public static string StemOf(string label, string fallback)
    {
        var stem = Path.GetFileNameWithoutExtension(label);
        var safe = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            safe.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        var text = Dashes().Replace(safe.ToString(), "-").Trim('-');
        if (text.Length > 48)
        {
            text = text[..48].TrimEnd('-');
        }

        return text.Length == 0 ? fallback : text;
    }

    /// <summary>An upload on its way in starts with a dot and is never a file of the folder.</summary>
    private bool IsStoreName(string name) => !name.StartsWith('.') && Accepts(name);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("-[0-9a-f]{10}$")]
    private static partial Regex HashSuffix();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();
}
