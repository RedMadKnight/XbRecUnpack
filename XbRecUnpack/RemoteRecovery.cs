﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace XbRecUnpack
{
    struct ManifestEntry
    {
        public string Variant;
        public string Action;
        public string BasePath;
        public string FilePath;
        public string CopyDestPath;
    }
    class RemoteRecovery
    {
        BinaryReader reader;
        List<ManifestEntry> Entries;
        List<long> cabHeaderPos;

        List<string> Variants = new List<string>();

        public RemoteRecovery(Stream exeStream)
        {
            reader = new BinaryReader(exeStream);
        }

        public bool Read()
        {
            Console.WriteLine("Scanning recovery EXE...");
            cabHeaderPos = new List<long>();
            while(reader.BaseStream.Position + 8 < reader.BaseStream.Length)
            {
                var currentPos = reader.BaseStream.Position;
                if (reader.ReadUInt64() == 0x4643534D)
                {
                    cabHeaderPos.Add(currentPos);
                    var size = reader.ReadUInt32();
                    reader.BaseStream.Position += (size - 0xC);
                }
                else
                    reader.BaseStream.Position = currentPos + 1;
            }

            if (cabHeaderPos.Count < 2)
            {
                Console.WriteLine("Error: couldn't find required CAB files inside recovery!");
                return false;
            }

            // Read the second cab in the file, contains some meta info about the other ones
            reader.BaseStream.Position = cabHeaderPos[1];
            var metaCab = new CabFile(reader.BaseStream);
            if (!metaCab.Read())
            {
                Console.WriteLine("Error: failed to read meta-cab!");
                return false;
            }

            // Remove the meta cab from cab header list...
            cabHeaderPos.RemoveAt(1);

            // Read the manifest file...
            var manifest = new CFFILE();
            if (!metaCab.GetEntry("manifest.csv", ref manifest, false))
            {
                Console.WriteLine("Error: failed to find manifest.csv inside meta-cab!");
                return false;
            }

            var manifestStream = metaCab.OpenFile(manifest);
            if (manifestStream == null)
                return false;

            string[] csv;
            using (var reader2 = new BinaryReader(manifestStream))
            {
                byte[] data = reader2.ReadBytes((int)reader2.BaseStream.Length);
                var str = Encoding.ASCII.GetString(data);
                csv = str.Replace("\r\n", "\n").Split(new char[]{ '\n' });
            }

            Variants = new List<string>();
            Entries = new List<ManifestEntry>();
            foreach(var line in csv)
            {
                if (string.IsNullOrEmpty(line))
                    continue;

                var parts = line.Split(new char[] { ',' });
                if (parts.Length < 6)
                    continue;

                if (parts[2] != "file" && parts[2] != "copy")
                    continue;

                var entry = new ManifestEntry();
                entry.Variant = parts[1];
                entry.Action = parts[2];
                entry.BasePath = parts[3];
                entry.FilePath = parts[4];
                entry.CopyDestPath = parts[5];

                if (string.IsNullOrEmpty(entry.Variant))
                    entry.Variant = "_All";

                Entries.Add(entry);

                if (!Variants.Contains(entry.Variant))
                    Variants.Add(entry.Variant);
            }

            Console.WriteLine($"Recovery contents:");
            Console.WriteLine($"{Entries.Count} files");
            Console.WriteLine($"{Variants.Count} variants:");
            foreach(var variant in Variants)
            {
                int numFiles = 0;
                foreach (var entry in Entries)
                    if (entry.Variant == variant)
                        numFiles++;

                Console.WriteLine($"  - {variant} ({numFiles} files)");
            }

            Console.WriteLine();

            // TODO: devices (need to read settings.ini from metaCab)

            return true;
        }

        public bool Extract(string destDirPath, bool listOnly = false)
        {
            if (Entries == null || cabHeaderPos == null || cabHeaderPos.Count <= 0 || Entries.Count <= 0)
                return false;

            // Read the main cab
            int curCab = 0;
            reader.BaseStream.Position = cabHeaderPos[curCab];
            CabFile mainCab = new CabFile(reader.BaseStream);
            if (!mainCab.Read())
                return false;

            int cabIndex = 0;
            int totalIndex = 1;
            foreach(var entry in Entries)
            {
                var variantPath = Path.Combine(entry.Variant, entry.FilePath);
                var entryPath = Path.Combine(destDirPath, variantPath);

                if (entry.Action == "file")
                {
                    if (cabIndex >= mainCab.Entries.Count)
                    {
                        // We've finished this cab, try loading the next one
                        curCab++;
                        cabIndex = 0;
                        if (curCab >= cabHeaderPos.Count)
                        {
                            Console.WriteLine("Error: couldn't find next cab file!");
                            break;
                        }

                        mainCab.Close();

                        reader.BaseStream.Position = cabHeaderPos[curCab];
                        mainCab = new CabFile(reader.BaseStream);
                        if (!mainCab.Read())
                        {
                            Console.WriteLine($"Error: failed to read CAB at offset 0x{cabHeaderPos[curCab]:X}");
                            break;
                        }
                    }

                    var cfEntry = mainCab.Entries[cabIndex];

                    if(cfEntry.Item2.ToLower() != entry.FilePath.ToLower())
                    {
                        Console.WriteLine("Warning: mismatch between manifest entry and cab entry!");
                    }

                    if (listOnly)
                        Console.WriteLine($"({totalIndex}/{Entries.Count}) {variantPath} ({Util.GetBytesReadable(cfEntry.Item1.cbFile)})");
                    else
                    {
                        var srcStream = mainCab.OpenFile(cfEntry.Item1);
                        if (srcStream == null)
                            return false;

                        if (!listOnly)
                        {
                            var destDir = Path.GetDirectoryName(entryPath);
                            if (!Directory.Exists(destDir))
                                Directory.CreateDirectory(destDir);
                        }

                        using (var destStream = File.Create(entryPath))
                        {
                            Console.WriteLine($"({totalIndex}/{Entries.Count}) {variantPath} ({Util.GetBytesReadable(cfEntry.Item1.cbFile)})");

                            long sizeRemain = cfEntry.Item1.cbFile;
                            byte[] buffer = new byte[32768];
                            while (sizeRemain > 0)
                            {
                                int read = (int)Math.Min((long)buffer.Length, sizeRemain);
                                srcStream.Read(buffer, 0, read);
                                destStream.Write(buffer, 0, read);
                                sizeRemain -= read;
                            }
                        }
                    }

                    cabIndex++;
                }
                else if(entry.Action == "copy")
                {
                    variantPath = Path.Combine(entry.Variant, entry.CopyDestPath);
                    string destPath = "";
                    if (!listOnly)
                    {
                        destPath = Path.Combine(destDirPath, variantPath);
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);
                    }

                    Console.WriteLine($"({totalIndex}/{Entries.Count}) {variantPath} (copy)");

                    if (!listOnly)
                    {
                        if (File.Exists(destPath))
                            File.Delete(destPath);

                        File.Copy(entryPath, destPath);
                    }
                }

                totalIndex++;
            }

            return true;
        }
    }
}