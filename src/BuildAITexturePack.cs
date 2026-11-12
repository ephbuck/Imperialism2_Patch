using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Imperialism2.Tools
{
    public class BuildAITexturePack
    {
        private struct ColorRGB
        {
            public byte R, G, B;
            public ColorRGB(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private static ColorRGB[] s_palette = new ColorRGB[256];
        private static byte[] s_paletteBytes = new byte[1024];

        // Velvet-smooth pasture green ramp: gentle, velvety continuous gradient
        private static readonly int[] s_velvetGrassRamp = new int[] {
            181, 186, 187, 194, 197
        };

        // Smooth desert sand ramp: warm, creamy natural dune gradients
        private static readonly int[] s_smoothDesertRamp = new int[] {
            132, 134, 136, 138, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 152, 153, 154, 155, 156
        };

        // Smooth tundra permafrost ramp: pale sage, lichen ochre, grey-green permafrost
        private static readonly int[] s_smoothTundraRamp = new int[] {
            75, 76, 80, 131, 133, 136, 137, 139, 140, 162, 163, 164, 167, 170, 171, 178, 190, 191, 195, 207
        };

        // Precomputed 64x64 canonical continuous ground turf (shared across all land tiles)
        private static byte[] s_canonicalTurf = new byte[4096];

        public static void Main(string[] args)
        {
            string baseDir = Directory.GetCurrentDirectory();
            string objDir = Path.Combine(baseDir, "obj", "all_tiles");
            string texDir = Path.Combine(baseDir, "textures");
            string outDir = Path.Combine(texDir, "ai_remastered");

            Directory.CreateDirectory(outDir);

            // 1. Read base palette from tile_10000.bmp
            string palTile = Path.Combine(objDir, "tile_10000.bmp");
            byte[] palFileBytes = File.ReadAllBytes(palTile);
            Array.Copy(palFileBytes, 54, s_paletteBytes, 0, 1024);
            for (int i = 0; i < 256; i++)
            {
                s_palette[i] = new ColorRGB(
                    palFileBytes[54 + i * 4 + 2],
                    palFileBytes[54 + i * 4 + 1],
                    palFileBytes[54 + i * 4]
                );
            }

            // 2. Precompute the canonical continuous base turf field G(x, y)
            PrecomputeCanonicalTurf();
            Console.WriteLine("Computed 100% seamless canonical continuous base turf field G(x, y).");

            // 3. Load master AI textures
            Bitmap bmpOcean = (Bitmap)Image.FromFile(Path.Combine(texDir, "master_ocean.jpg"));
            Bitmap bmpDesert = (Bitmap)Image.FromFile(Path.Combine(texDir, "master_desert.jpg"));
            Bitmap bmpTundra = (Bitmap)Image.FromFile(Path.Combine(texDir, "master_tundra.jpg"));

            Console.WriteLine("Loaded master textures: Ocean, Desert, Tundra.");

            // 4. Process all 218 tiles
            string[] files = Directory.GetFiles(objDir, "tile_*.bmp");
            int totalProcessed = 0;

            foreach (string file in files)
            {
                string fn = Path.GetFileNameWithoutExtension(file);
                int id = int.Parse(fn.Replace("tile_", ""));
                string destFile = Path.Combine(outDir, Path.GetFileName(file));

                byte[] origBytes = File.ReadAllBytes(file);
                int biW = BitConverter.ToInt32(origBytes, 18);
                int biH = BitConverter.ToInt32(origBytes, 22);

                // If not standard 64x64, copy as-is (e.g. 25x64 strips 570..577)
                if (biW != 64 || biH != 64)
                {
                    File.Copy(file, destFile, true);
                    continue;
                }

                // --- TILE CATEGORY HANDLING ---

                // A. Pure Base Grass / Plains (10000, 10108)
                if (id == 10000 || id == 10108)
                {
                    byte[] aiTile = (byte[])s_canonicalTurf.Clone();
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // B. Pure Ocean (10005)
                else if (id == 10005)
                {
                    byte[] aiTile = GenerateBaseOceanTile(bmpOcean, id * 19);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // C. Desert Dunes (10006, 10025) - Smooth sand center with isometric diamond GREEN EDGES blending to plains!
                else if (id == 10006 || id == 10025)
                {
                    byte[] aiTile = GenerateBiomeWithGreenEdges(bmpDesert, id * 23, s_smoothDesertRamp, 0.80f, 1.20f);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // D. Tundra (10100, 10101) - Cold Arctic permafrost center with isometric diamond GREEN EDGES blending to plains!
                else if (id == 10100 || id == 10101)
                {
                    byte[] aiTile = GenerateBiomeWithGreenEdges(bmpTundra, id * 43, s_smoothTundraRamp, 0.80f, 1.20f);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // E. Furry Tundra (10113, 10114) - Beavers & skulls on Arctic permafrost with isometric green edges!
                else if (id == 10113 || id == 10114)
                {
                    byte[] aiTile = GenerateCompositeTundra(origBytes, bmpTundra, id * 31);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // F. Coastlines & Islands (10034, 10035, 10038, 10039, 10040, 10041, 10094..10097)
                else if (IsCoastOrIslandTile(id))
                {
                    byte[] aiTile = GenerateCompositeCoast(origBytes, bmpOcean, id * 29);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // G. Land Tiles with Sprites / Formations (Mountains, Sheep Hills, Fertile Hills, Barren Hills, Forests, Woods, Cattle, Horses, Wheat, Plantations, Swamps)
                else if (IsLandFeatureTile(id))
                {
                    byte[] aiTile = GroundFeatureOnCanonicalTurf(origBytes);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                // H. Transition Overlays (10300..10341, 10400..10427) - Ground any pasture grass on canonical turf while preserving transparency!
                else if (IsTransitionOverlayTile(id))
                {
                    byte[] aiTile = GroundOverlayOnCanonicalTurf(origBytes);
                    SaveTile(origBytes, aiTile, destFile);
                    totalProcessed++;
                }
                else
                {
                    // Copy overlay / transparent tiles as-is
                    File.Copy(file, destFile, true);
                }
            }

            bmpOcean.Dispose();
            bmpDesert.Dispose();
            bmpTundra.Dispose();

            Console.WriteLine(string.Format("Successfully generated interconnected AI Texture Pack: {0} tiles remastered.", totalProcessed));
        }

        // Precompute canonical 64x64 continuous base turf G(x, y)
        // Mathematically seamless across all 4 boundaries: G(0, y) == G(64, y), G(x, 0) == G(x, 64)
        private static void PrecomputeCanonicalTurf()
        {
            for (int y = 0; y < 64; y++)
            {
                int srcY = 63 - y; // BMP bottom-up
                double ty = (srcY * 2.0 * Math.PI) / 64.0;
                for (int x = 0; x < 64; x++)
                {
                    double tx = (x * 2.0 * Math.PI) / 64.0;

                    // Multi-frequency periodic continuous field
                    double v = 0.40 * Math.Sin(2.0 * tx) * Math.Cos(2.0 * ty) +
                               0.30 * Math.Sin(4.0 * tx + 2.0 * ty) * Math.Cos(2.0 * tx - 4.0 * ty) +
                               0.15 * Math.Cos(6.0 * tx - 6.0 * ty);

                    int pos = (int)(((v + 0.65) / 1.30) * (s_velvetGrassRamp.Length - 1));
                    if (pos < 0) pos = 0;
                    if (pos >= s_velvetGrassRamp.Length) pos = s_velvetGrassRamp.Length - 1;

                    s_canonicalTurf[y * 64 + x] = (byte)s_velvetGrassRamp[pos];
                }
            }
        }

        // Generates an Isometric Diamond Biome Tile (Desert, Tundra) with organic green edges fading into canonical base grass
        private static byte[] GenerateBiomeWithGreenEdges(Bitmap master, int offsetSeed, int[] biomeRamp, float innerR, float outerR)
        {
            int cropSize = 256;
            int cropX = Math.Abs((offsetSeed * 47) % Math.Max(1, master.Width - cropSize));
            int cropY = Math.Abs((offsetSeed * 79) % Math.Max(1, master.Height - cropSize));

            using (Bitmap cropped = master.Clone(new Rectangle(cropX, cropY, cropSize, cropSize), master.PixelFormat))
            using (Bitmap scaled = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(cropped, 0, 0, 64, 64);
                }

                byte[] pixels = new byte[4096];
                for (int y = 0; y < 64; y++)
                {
                    int srcY = 63 - y;
                    double dy = Math.Abs(srcY - 31.5) / 22.0;

                    for (int x = 0; x < 64; x++)
                    {
                        double dx = Math.Abs(x - 31.5) / 30.0;
                        // Isometric diamond distance + subtle organic wavy boundary
                        double distDiamond = dx + dy;
                        double theta = Math.Atan2(srcY - 31.5, x - 31.5);
                        double dist = distDiamond + 0.10 * Math.Sin(4.0 * theta + 0.8) + 0.05 * Math.Cos(6.0 * theta);

                        Color c = scaled.GetPixel(x, srcY);
                        int biomeIdx = QuantizeToRamp(c.R, c.G, c.B, biomeRamp);
                        ColorRGB biomeCol = s_palette[biomeIdx];

                        byte grassIdx = s_canonicalTurf[y * 64 + x];
                        ColorRGB grassCol = s_palette[grassIdx];

                        // Blend biome center into green grass at the perimeter
                        if (dist <= innerR)
                        {
                            pixels[y * 64 + x] = (byte)biomeIdx; // 100% Biome in center
                        }
                        else if (dist >= outerR)
                        {
                            pixels[y * 64 + x] = grassIdx; // 100% Green grass around edges!
                        }
                        else
                        {
                            // Smooth cosine interpolation
                            double t = 0.5 * (1.0 + Math.Cos(Math.PI * (dist - innerR) / (outerR - innerR)));
                            int r = (int)(t * biomeCol.R + (1.0 - t) * grassCol.R);
                            int gCol = (int)(t * biomeCol.G + (1.0 - t) * grassCol.G);
                            int b = (int)(t * biomeCol.B + (1.0 - t) * grassCol.B);

                            pixels[y * 64 + x] = QuantizeToClosest(r, gCol, b);
                        }
                    }
                }
                return pixels;
            }
        }

        // Grounds any land feature tile (Mountains, Hills, Sheep, Forests, Cattle, Wheat, etc.)
        // by replacing background grass with the canonical continuous turf G(x, y).
        // Preserves rocks, peaks, trees, animals, fences, furrows, and hill contours 100%.
        private static byte[] GroundFeatureOnCanonicalTurf(byte[] origFileBytes)
        {
            byte[] result = new byte[4096];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int idx = y * 64 + x;
                    byte origPalIdx = origFileBytes[1078 + idx];
                    ColorRGB origColor = s_palette[origPalIdx];

                    // Original pasture background grass in Imperialism II is palette indices 167..210
                    // All terrain features (dark foliage of trees 211..221, rocks 30..166, sheep/cows/horses 66..83, wheat 39..46)
                    // lie strictly outside this range, ensuring 100% feature preservation!
                    bool isGrass = (origPalIdx >= 167 && origPalIdx <= 210);

                    if (!isGrass)
                    {
                        // Feature pixel: mountain rock, tree canopy, sheep, cow, wheat, fence
                        result[idx] = origPalIdx;
                    }
                    else
                    {
                        // Background pixel: replace with canonical continuous turf!
                        result[idx] = s_canonicalTurf[idx];
                    }
                }
            }
            return result;
        }

        // Furry Tundra: Arctic permafrost with green edges, preserving beavers and skulls
        private static byte[] GenerateCompositeTundra(byte[] origFileBytes, Bitmap masterTundra, int seed)
        {
            byte[] baseTundra = GenerateBiomeWithGreenEdges(masterTundra, seed, s_smoothTundraRamp, 0.80f, 1.20f);
            byte[] result = new byte[4096];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int idx = y * 64 + x;
                    byte origPalIdx = origFileBytes[1078 + idx];
                    ColorRGB origColor = s_palette[origPalIdx];

                    // Beaver fur: orange/brown (R > 180, B < 60), skull: bright white (R,G,B > 220)
                    bool isSprite = (origColor.R > 180 && origColor.G < 140 && origColor.B < 60) ||
                                    (origColor.R > 215 && origColor.G > 215 && origColor.B > 215);

                    if (isSprite)
                    {
                        result[idx] = origPalIdx;
                    }
                    else
                    {
                        result[idx] = baseTundra[idx];
                    }
                }
            }
            return result;
        }

        // Coastlines & Islands: Water is ocean caustics, land is canonical continuous turf!
        private static byte[] GenerateCompositeCoast(byte[] origFileBytes, Bitmap masterOcean, int seed)
        {
            byte[] aiOcean = GenerateBaseOceanTile(masterOcean, seed);
            byte[] result = new byte[4096];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int idx = y * 64 + x;
                    byte origPalIdx = origFileBytes[1078 + idx];
                    ColorRGB origColor = s_palette[origPalIdx];

                    bool isWater = (origPalIdx == 25) ||
                                   (origColor.B > 110 && origColor.B > origColor.R + 25 && origColor.G < 110);

                    if (isWater)
                    {
                        result[idx] = aiOcean[idx]; // Shimmering ocean caustics
                    }
                    else
                    {
                        // If it was grass on the coastline/island, use canonical turf!
                        bool isGrass = (origPalIdx >= 167 && origPalIdx <= 210);
                        if (isGrass)
                        {
                            result[idx] = s_canonicalTurf[idx];
                        }
                        else
                        {
                            result[idx] = origPalIdx; // Coast cliff / beach sand
                        }
                    }
                }
            }
            return result;
        }

        // Transition Overlays (10300..10341, 10400..10427): Keep transparent pixels (253) and features intact;
        // ground any pasture grass on canonical turf so slopes blend into surroundings seamlessly!
        private static byte[] GroundOverlayOnCanonicalTurf(byte[] origFileBytes)
        {
            byte[] result = new byte[4096];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int idx = y * 64 + x;
                    byte origPalIdx = origFileBytes[1078 + idx];

                    // If transparent (253) or feature (rock, water, etc.), keep as-is
                    if (origPalIdx == 253 || origPalIdx < 167 || origPalIdx > 210)
                    {
                        result[idx] = origPalIdx;
                    }
                    else
                    {
                        // Pasture grass in transition overlay: align to canonical turf!
                        result[idx] = s_canonicalTurf[idx];
                    }
                }
            }
            return result;
        }

        // Helper: Is this tile a transition overlay tile?
        private static bool IsTransitionOverlayTile(int id)
        {
            return (id >= 10300 && id <= 10341) ||
                   (id >= 10400 && id <= 10427);
        }

        // Ocean Base Tile with caustic ripple depth
        private static byte[] GenerateBaseOceanTile(Bitmap master, int offsetSeed)
        {
            int cropSize = 256;
            int cropX = Math.Abs((offsetSeed * 47) % Math.Max(1, master.Width - cropSize));
            int cropY = Math.Abs((offsetSeed * 79) % Math.Max(1, master.Height - cropSize));

            using (Bitmap cropped = master.Clone(new Rectangle(cropX, cropY, cropSize, cropSize), master.PixelFormat))
            using (Bitmap scaled = new Bitmap(64, 64))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(cropped, 0, 0, 64, 64);
                }

                byte[] pixels = new byte[4096];
                int[] oceanRamp = new int[] { 228, 227, 25, 225, 224, 223, 222 };
                for (int y = 0; y < 64; y++)
                {
                    int srcY = 63 - y;
                    for (int x = 0; x < 64; x++)
                    {
                        Color c = scaled.GetPixel(x, srcY);
                        pixels[y * 64 + x] = QuantizeToRamp(c.R, c.G, c.B, oceanRamp);
                    }
                }
                return pixels;
            }
        }

        // Helper: Is this tile a coastline or island tile?
        private static bool IsCoastOrIslandTile(int id)
        {
            return (id >= 10034 && id <= 10035) ||
                   (id >= 10038 && id <= 10041) ||
                   (id >= 10094 && id <= 10097);
        }

        // Helper: Is this a land tile with features (Mountains, Hills, Sheep, Forests, Wheat, Cattle, Horses, Plantations, Swamps)?
        private static bool IsLandFeatureTile(int id)
        {
            // Old World & New World Mountains: 10003, 10011, 10014, 10019, 10020, 10027, 10030, 10031, 10036
            // Hills & Sheep: 10002, 10008, 10010, 10017, 10018, 10028, 10029, 10032
            // Forests & Woods: 10001, 10015, 10016, 10037, 10102, 10103, 10106, 10107
            // Farms & Ranches: 10007, 10009, 10012, 10013, 10026
            // Plantations: 10109, 10110, 10111, 10112, 10115
            // Swamps & Fens: 10004, 10021, 10022, 10033
            return (id >= 10001 && id <= 10004) ||
                   (id >= 10007 && id <= 10024) ||
                   (id >= 10026 && id <= 10033) ||
                   (id >= 10036 && id <= 10037) ||
                   (id >= 10102 && id <= 10107) ||
                   (id >= 10109 && id <= 10112) ||
                   (id == 10115);
        }

        private static byte QuantizeToRamp(byte r, byte g, byte b, int[] ramp)
        {
            int bestIdx = ramp[0];
            int bestDist = int.MaxValue;

            foreach (int i in ramp)
            {
                int dr = r - s_palette[i].R;
                int dg = g - s_palette[i].G;
                int db = b - s_palette[i].B;

                int dist = (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            return (byte)bestIdx;
        }

        private static byte QuantizeToClosest(int r, int g, int b)
        {
            int bestIdx = 0;
            int bestDist = int.MaxValue;

            for (int i = 0; i < 256; i++)
            {
                if (s_palette[i].R == 255 && s_palette[i].G == 0 && s_palette[i].B == 255) continue;

                int dr = r - s_palette[i].R;
                int dg = g - s_palette[i].G;
                int db = b - s_palette[i].B;

                int dist = (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            return (byte)bestIdx;
        }

        private static void SaveTile(byte[] origHeaderBytes, byte[] newPixelBytes, string destPath)
        {
            byte[] outBytes = new byte[5174];
            Array.Copy(origHeaderBytes, 0, outBytes, 0, 1078);
            Array.Copy(newPixelBytes, 0, outBytes, 1078, 4096);
            File.WriteAllBytes(destPath, outBytes);
        }
    }
}
