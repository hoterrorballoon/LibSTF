// Sonic The Fighters model injector library
// Copyright (C) 2025 bekzii
// 
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version.
// 
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Lesser Public License for more
// details.
// 
// You should have received a copy of the GNU Lesser General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibSTF
{
    public static class ModelInject
    {
        class InjectInfo
        {
            public string ModelPath;
            public List<int> Ids = new List<int>();

            public InjectInfo(string modelPath)
            {
                this.ModelPath = modelPath;
            }
        }

        class DataInfo
        {
            public InjectInfo Info;
            public int Address;
            public byte[] Data;
            public ulong Hash;

            public DataInfo(InjectInfo info, int address, byte[] data, ulong hash)
            {
                this.Info = info;
                this.Address = address;
                this.Data = data;
                this.Hash = hash;
            }
        }

        // rom_data address - a table of rom addresses to the model's
        // texture points, texture headers, and polygons respectively
        const int MODEL_TABLE_ADDR = 0xE0004;

        // these are safe addresses for new data to be inserted
        const int POL_BASE_ADDR = 0xEC25E0;
        const int TEX_BASE_ADDR = 0x790000;

        static Queue<InjectInfo> s_InjectQueue = new Queue<InjectInfo>();

        /// <summary>
        /// Whether to print debug logs of inject events
        /// </summary>
        public static bool Verbose = false;

        static void VerboseLog(string str)
        {
            if (!Verbose) return;
            Console.WriteLine(str);
        }

        // simple C# implementation of FNV-1a
        static ulong Hash(byte[] data)
        {
            ulong hash = 0xcbf29ce484222325;
            foreach (byte b in data)
            {
                hash = (hash ^ b) * 0x00000100000001b3;
            }
            return hash;
        }

        static InjectInfo SummonModelInfo(string modelPath)
        {
            InjectInfo info = null;
            // see if we can reuse the info for this model
            foreach (InjectInfo item in s_InjectQueue)
            {
                if (item.ModelPath == modelPath)
                {
                    info = item;
                    break;
                }
            }
            // make a new info if we can't
            if (info == null)
            {
                info = new InjectInfo(modelPath);
                s_InjectQueue.Enqueue(info);
            }

            return info;
        }

        /// <summary>
        /// Adds a model to the inject queue
        /// </summary>
        /// <param name="id">ID to assign this model to</param>
        /// <param name="modelPath">File path to the model, without an extension</param>
        static public void AddModel(int id, string modelPath)
        {
            InjectInfo info = SummonModelInfo(modelPath);
            info.Ids.Add(id);
        }

        /// <summary>
        /// Adds a model to the inject queue. Call InjectModels() to write the entire queue to the ROMs
        /// </summary>
        /// <param name="ids">List of IDs to assign this model to</param>
        /// <param name="modelPath">File path to the model, without an extension</param>
        static public void AddModel(int[] ids, string modelPath)
        {
            InjectInfo info = SummonModelInfo(modelPath);
            info.Ids.AddRange(ids);
        }

        static int GetModelTableAddr(int id)
        {
            return MODEL_TABLE_ADDR + (id * 0x10);
        }

        static int GetPolAddr(int addr)
        {
            addr += 0x2000010;
            addr /= 4;
            return addr;
        }

        static int GetTexAddr(int addr)
        {
            addr /= 2;
            return addr;
        }

        static void WriteInt(FileStream stream, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            stream.Write(bytes, 0, bytes.Length);
        }

        // round up to the nearest 4-byte boundary
        static int Align4(int value)
        {
            return (value + 3) & ~0x3;
        }

        // Write padding to align data to 4 bytes, plus 4 extra bytes
        static void WritePadding(FileStream stream, bool extraBytes)
        {
            if (extraBytes)
            {
                stream.WriteByte(0x00);
                stream.WriteByte(0x00);
                stream.WriteByte(0x00);
                stream.WriteByte(0x00);
            }

            int align = Align4((int)stream.Position) - (int)stream.Position;
            for (int i = 0; i < align; i++)
            {
                stream.WriteByte(0x00);
            }
        }

        static DataInfo FindDuplicateData(List<DataInfo> injects, ulong hash, byte[] data)
        {
            foreach (var inject in injects)
            {
                if (inject.Hash != hash) continue;
                if (inject.Data.Length != data.Length) continue;
                if (!inject.Data.SequenceEqual(data)) continue;
                return inject;
            }
            return null;
        }

        /// <summary>
        /// Injects all of the models in the inject queue
        /// </summary>
        /// <param name="romPath">file path to the directory containing rom_XXX.bin files</param>
        static public void InjectModels(string romPath)
        {
            try
            {
                using (var f_Data = new FileStream(Path.Combine(romPath, "rom_data.bin"), FileMode.Open))
                using (var f_Pol = new FileStream(Path.Combine(romPath, "rom_pol.bin"), FileMode.Open))
                using (var f_Tex = new FileStream(Path.Combine(romPath, "rom_tex.bin"), FileMode.Open))
                {
                    long maxSizePol = f_Pol.Length;
                    long maxSizeTex = f_Tex.Length;

                    int polOffset = POL_BASE_ADDR;
                    int texOffset = TEX_BASE_ADDR;

                    List<DataInfo> polInjects = new List<DataInfo>();
                    List<DataInfo> texInjects = new List<DataInfo>();

                    int totalSizePol = 0, totalSizeTex = 0;

                    while (s_InjectQueue.Count > 0)
                    {
                        InjectInfo info = s_InjectQueue.Dequeue();

                        VerboseLog($"Injecting model '{Path.GetFileNameWithoutExtension(info.ModelPath)}' into IDs [ {string.Join(",", info.Ids.ToArray())} ] at POL_BASE + 0x{(polOffset - POL_BASE_ADDR):X4} & TEX_BASE + 0x{(texOffset - TEX_BASE_ADDR):X4} ...");

                        byte[] dataMdl = File.ReadAllBytes(info.ModelPath + ".stfmdl");
                        byte[] dataMat = File.ReadAllBytes(info.ModelPath + ".stfmat");
                        byte[] dataUvs = File.ReadAllBytes(info.ModelPath + ".stfuvs");

                        int addrMdl, addrMat, addrUvs;
                        int sizePol = 0, sizeTex = 0;

                        // Inject POLYGON data

                        ulong hashMdl = Hash(dataMdl);
                        DataInfo injectMdl = FindDuplicateData(polInjects, hashMdl, dataMdl);
                        if (injectMdl != null)
                        {
                            VerboseLog($"  (Reusing polygon data from {Path.GetFileNameWithoutExtension(injectMdl.Info.ModelPath)})");
                            addrMdl = injectMdl.Address;
                        }
                        else
                        {
                            VerboseLog("  Writing polygon data...");

                            // leave 4 byte padding
                            int size = Align4(dataMdl.Length) + 4;
                            if ((polOffset + size) > maxSizePol)
                            {
                                throw new OutOfMemoryException("Pol data overflow");
                            }

                            addrMdl = polOffset;
                            polOffset += size;

                            sizePol += size;

                            f_Pol.Seek(addrMdl, SeekOrigin.Begin);
                            f_Pol.Write(dataMdl, 0, dataMdl.Length);
                            WritePadding(f_Pol, true);

                            polInjects.Add(new DataInfo(info, addrMdl, dataMdl, hashMdl));
                        }

                        // Inject TEX HEADER data

                        ulong hashMat = Hash(dataMat);
                        DataInfo injectMat = FindDuplicateData(texInjects, hashMat, dataMat);
                        if (injectMat != null)
                        {
                            VerboseLog($"  (Reusing texture header data from {Path.GetFileNameWithoutExtension(injectMat.Info.ModelPath)})");
                            addrMat = injectMat.Address;
                        }
                        else
                        {
                            VerboseLog("  Writing texture header data...");

                            // leave 4 byte padding
                            int size = Align4(dataMat.Length) + 4;
                            if ((texOffset + size) > maxSizeTex)
                            {
                                throw new OutOfMemoryException("Tex data overflow (mat)");
                            }

                            addrMat = texOffset;
                            texOffset += size;

                            sizeTex += size;

                            f_Tex.Seek(addrMat, SeekOrigin.Begin);
                            f_Tex.Write(dataMat, 0, dataMat.Length);
                            WritePadding(f_Tex, true);

                            texInjects.Add(new DataInfo(info, addrMat, dataMat, hashMat));
                        }

                        // Inject TEX POINTS data

                        ulong hashUvs = Hash(dataUvs);
                        DataInfo injectUvs = FindDuplicateData(texInjects, hashUvs, dataUvs);
                        if (injectUvs != null)
                        {
                            VerboseLog($"  (Reusing texture point data from {Path.GetFileNameWithoutExtension(injectUvs.Info.ModelPath)})");
                            addrUvs = injectUvs.Address;
                        }
                        else
                        {
                            VerboseLog("  Writing texture point data...");

                            // leave 4 byte padding
                            int size = Align4(dataUvs.Length) + 4;
                            if ((texOffset + size) > maxSizeTex)
                            {
                                throw new OutOfMemoryException("Tex data overflow (uvs)");
                            }

                            addrUvs = texOffset;
                            texOffset += size;

                            sizeTex += size;

                            f_Tex.Seek(addrUvs, SeekOrigin.Begin);
                            f_Tex.Write(dataUvs, 0, dataUvs.Length);
                            WritePadding(f_Tex, true);

                            texInjects.Add(new DataInfo(info, addrUvs, dataUvs, hashUvs));
                        }

                        // Write DATA ADDRESSES

                        VerboseLog("  Writing data addresses...");

                        foreach (int id in info.Ids)
                        {
                            f_Data.Seek(GetModelTableAddr(id), SeekOrigin.Begin);
                            WriteInt(f_Data, GetTexAddr(addrUvs)); // write tex points offset
                            WriteInt(f_Data, GetTexAddr(addrMat)); // write tex header offset
                            WriteInt(f_Data, GetPolAddr(addrMdl)); // write poly offset
                        }

                        VerboseLog($"    Done! Size of pol data: 0x{sizePol:X4} Size of tex data: 0x{sizeTex:X4}");

                        totalSizePol += sizePol;
                        totalSizeTex += sizeTex;
                    }

                    VerboseLog($"All models injected! Total size of pol data: 0x{totalSizePol:X4} Total size of tex data: 0x{totalSizeTex:X4}");
                }
            }
            finally
            {
                s_InjectQueue.Clear();
            }
        }
    }
}
