using fNbt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TryashtarUtils.Nbt;
using TryashtarUtils.Utility;

namespace NbtStudio
{
    // represents a loadable and saveable NBT file
    // uses fNbt.NbtFile to do the work reading/writing binary data to disk, but can also read/write SNBT without using one
    public class NbtFile : IFile
    {
        public string Path { get; private set; }
        public event Action OnSaved;
        public NbtTag RootTag { get; private set; }
        public T GetRootTag<T>() where T : NbtTag => RootTag as T;
        public ExportSettings ExportSettings { get; private set; }
        public bool CanSave => Path is not null && ExportSettings is not null;
        public bool CanRefresh => CanSave;
        public bool HasUnsavedChanges { get; private set; } = false;

        private NbtFile(string path, NbtTag root, ExportSettings settings)
        {
            Path = path;
            SetRoot(root);
            ExportSettings = settings;
        }

        public NbtFile() : this(new NbtCompound(""))
        { }

        public NbtFile(NbtTag root)
        {
            if (root.Name is null)
                root.Name = "";
            SetRoot(root);
            Path = null;
            ExportSettings = null;
            HasUnsavedChanges = true;
        }

        private void SetRoot(NbtTag root)
        {
            RootTag = root;
            RootTag.OnChanged += _ => HasUnsavedChanges = true;
        }

        private static bool LooksSuspicious(string name)
        {
            if (name is null)
                return false;
            foreach (var ch in name)
            {
                if (Char.IsControl(ch))
                    return true;
            }
            return false;
        }

        private static bool LooksSuspicious(NbtTag tag)
        {
            if (LooksSuspicious(tag.Name))
                return true;
            if (tag is NbtString str && LooksSuspicious(str.Value))
                return true;
            if (tag is NbtContainerTag container && container.Any(x => LooksSuspicious(x.Name)))
                return true;
            return false;
        }

        public static IFailable<NbtFile> TryCreate(string path)
        {
            var methods = new Func<IFailable<NbtFile>>[]
            {
                () => TryCreateFromSnbt(path), // SNBT
                () => TryCreateFromNbt(path, NbtCompression.AutoDetect, big_endian: true), // java files
                () => TryCreateFromNbt(path, NbtCompression.AutoDetect, big_endian: false), // bedrock files
                () => TryCreateFromNbt(path, NbtCompression.AutoDetect, varint: true), // some other bedrock files
                () => TryCreateFromNbt(path, NbtCompression.AutoDetect, big_endian: false, bedrock_header: true) // bedrock level.dat files
            };
            return TryVariousMethods(methods, x => (x.RootTag is NbtContainerTag c && c.Count == 0) || LooksSuspicious(x.RootTag));
        }

        public static IFailable<NbtFile> TryVariousMethods(IEnumerable<Func<IFailable<NbtFile>>> methods, Predicate<NbtFile> suspicious)
        {
            // try loading a file a few different ways
            // if loading fails or looks suspicious, try a different way
            // if all loads are suspicious, choose the first that didn't fail
            var attempted = new List<IFailable<NbtFile>>();
            foreach (var method in methods)
            {
                var result = method();
                if (!result.Failed && !suspicious(result.Result))
                    return result;
                attempted.Add(result);
            }
            // everything was suspicious, pick the first that didn't fail
            foreach (var attempt in attempted)
            {
                if (!attempt.Failed)
                    return attempt;
            }
            // everything failed, sob!
            return FailableFactory.Aggregate(attempted.ToArray());
        }

        public static Failable<NbtFile> TryCreateFromSnbt(string path)
        {
            return new Failable<NbtFile>(() => CreateFromSnbt(path), "Load as SNBT");
        }

        public static NbtFile CreateFromSnbt(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                char[] firstchar = new char[1];
                reader.ReadBlock(firstchar, 0, 1);
                if (firstchar[0] != '{') // optimization to not load in huge files
                    throw new FormatException("File did not begin with a '{'");
                var text = firstchar[0] + reader.ReadToEnd();
                var tag = SnbtParser.Parse(text, named: false);
                if (tag is not NbtCompound compound)
                    throw new FormatException("File did not contain an NBT compound");
                compound.Name = "";
                var file = new fNbt.NbtFile(compound);
                return new NbtFile(path, file.RootTag, ExportSettings.AsSnbt(!text.Contains("\n"), System.IO.Path.GetExtension(path) == ".json"));
            }
        }

        public static Failable<NbtFile> TryCreateFromNbt(string path, NbtCompression compression, bool big_endian = true, bool bedrock_header = false, bool varint = false)
        {
            return new Failable<NbtFile>(() => CreateFromNbt(path, compression, big_endian, bedrock_header, varint), $"Load as NBT (compression: {compression}, big endian: {big_endian}, bedrock header: {bedrock_header}, varint: {varint})");
        }

        public static Failable<NbtFile> TryCreateFromExportSettings(string path, ExportSettings settings)
        {
            if (settings.Snbt)
                return TryCreateFromSnbt(path);
            else
                return TryCreateFromNbt(path, settings.Compression, settings.BigEndian, settings.BedrockHeader);
        }

        public static NbtFile CreateFromNbt(string path, NbtCompression compression, bool big_endian = true, bool bedrock_header = false, bool varint = false)
        {
            var file = new fNbt.NbtFile();
            file.BigEndian = big_endian;
            file.UseVarInt = varint;
            using (var reader = File.OpenRead(path))
            {
                if (bedrock_header)
                {
                    var header = new byte[8];
                    reader.Read(header, 0, header.Length);
                }
                file.LoadFromStream(reader, compression);
            }
            if (file.RootTag is null)
                throw new FormatException("File had no root tag");
            if (file.RootTag is not NbtCompound)
                throw new FormatException("File did not contain an NBT compound");

            return new NbtFile(path, file.RootTag, ExportSettings.AsNbt(file.FileCompression, big_endian, bedrock_header));
        }

        public void Save()
        {
            ExportSettings.Export(Path, RootTag);
            HasUnsavedChanges = false;
            OnSaved?.Invoke();
        }

        public void SaveAs(string path)
        {
            Path = path;
            Save();
        }

        public void SaveAs(string path, ExportSettings settings)
        {
            Path = path;
            ExportSettings = settings;
            Save();
        }

        public void Refresh()
        {
            var current = TryCreateFromExportSettings(Path, ExportSettings).Result.RootTag;
            RootTag.SetEqualTo(current);
            HasUnsavedChanges = false;
        }

        public void Move(string path)
        {
            if (Path is not null)
            {
                File.Move(Path, path);
                Path = path;
            }
        }
    }
}
