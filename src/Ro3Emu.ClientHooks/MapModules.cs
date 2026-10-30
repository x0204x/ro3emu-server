using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Map config modules (Config/Map/&lt;MapResource._kResource&gt;.lua) that this client build does not ship, put into
    /// package.loaded so that require finds them.
    /// <para>
    /// Every scene load requires its map module: LC_Scene_Sync.LoadSceneEditorConfig (LC_Scene_Sync.lua:1497-1511) and
    /// LC_DunGeon_Base.LoadSceneEditorConfig (LC_DunGeon_Base.lua:737-751) call _loadMapBaseConfig(MapResource._kResource),
    /// a bare require (LC_Scene_Sync.lua:2427-2447). The client's Lua only comes from the recovery manifest
    /// (StreamingAssets/Recovery/recovery-compatibility-manifest.json, signed): LA_LuaLoader.Obf_HD cuts the module name to
    /// its last path part and RecoveryLuaSourceResolver.TryResolve refuses any name the manifest does not list
    /// ("reconstructed_module_not_admitted")。目前客戶端仍未提供領土戰 36 張地圖與兩張賽馬地圖；
    /// 這些 require 會在 SceneLoadManager callback 內失敗，使載入停住後斷線。
    /// Genesis 2026.10.29.1 已正式提供 sc_pm_gef_dun02_001；不得先用補件遮蔽原件。
    /// </para>
    /// <para>
    /// The values are the original modules, not guesses: the game's own chunks are still in the VFS under the module name
    /// (a "bytes" asset in the client's 0x1E Lua 5.4 format), decoded and read by tools/WorldData/mapmodule.js, which
    /// prints the entries below. kNavMeshExporter is "map/bin/&lt;name&gt;.bin" and ItemName the name itself in all of them
    /// (the tool checks it). What the client reads of a map module: MiniMapInfo[i] (Lua_DB_Helper.GetMiniMapCameraInfo and
    /// GetCameraInfo, Lua_DB_Helper.lua:290-462: the minimap camera and its projection) and ItemName (only in a warning when
    /// P is zero, Lua_DB_Helper.lua:443). kMapId and kNavMeshExporter have no live reader (LC_Scene_Sync.lua:2636 reads
    /// kMapId from the scene-editor module, which never has one; LC_NpcCollider has no caller); they are kept as shipped.
    /// </para>
    /// <para>
    /// A camera is "w h  P[0..15]  pos.x y z  rot.x y z w  orthoSize aspect"; a number with a '.' or an exponent is a Lua
    /// float, any other an integer, as the original constants are. To add a map: node tools/WorldData/mapmodule.js &lt;name&gt;.
    /// </para>
    /// </summary>
    public static class MapModules
    {
        public sealed class MapModule
        {
            public readonly string Name;
            public readonly long MapId;
            public readonly string[] Cameras;

            public MapModule(string name, long mapId, params string[] cameras)
            {
                Name = name;
                MapId = mapId;
                Cameras = cameras;
            }
        }

        internal static readonly MapModule[] Modules =
        {
            new MapModule("sc_pm_saima_001", 1378433040,
                "1024 1024  0.004446025 0 0 0 0 0.004446025 0 0 0 0 -0.008364351 0 0 0 -0.2965581 1  230 0 199  0.7071068 0 0 0.7071068  224.92 1"),
            new MapModule("sc_pm_saima_002", 1378433039,
                "1024 1024  0.005 0 0 0 0 0.005 0 0 0 0 -0.01057641 0 0 0 -0.5494447 1  200 100 200  0.7071068 0 0 0.7071068  200 1"),
            new MapModule("sc_gvg_lingtuzhan_001_1", 1851643956,
                "1024 1024  0.002590002 0 0 0 0 0.002590002 0 0 0 0 -0.001512059 0 0 0 -0.082634 1  471 -78 491  0.7071068 0 0 0.7071068  386.1 1"),
            new MapModule("sc_gvg_lingtuzhan_001_2", 1851643957,
                "1024 1024  0.00307787 0 0 0 0 0.00307787 0 0 0 0 -0.001700391 0 0 0 0.3637137 1  437 89.8 406.1  0.7071068 0 0 0.7071068  324.9 1"),
            new MapModule("sc_gvg_lingtuzhan_001_3", 1851643958,
                "1024 1024  0.004444445 0 0 0 0 0.004444445 0 0 0 0 -0.004017678 0 0 0 -0.1149056 1  224.8 82 224.91  0.7071068 0 0 0.7071068  225 1"),
            new MapModule("sc_gvg_lingtuzhan_001_4", 1851643951,
                "1024 1024  0.003846154 0 0 0 0 0.003846154 0 0 0 0 -0.01333333 0 0 0 -1 1  460 81 479  0.7071068 0 0 0.7071068  260 1"),
            new MapModule("sc_gvg_lingtuzhan_001_5", 1851643952,
                "1024 1024  0.003030303 0 0 0 0 0.003030303 0 0 0 0 -0.001765615 0 0 0 0.1578901 1  409.3 75.2 466  0.7071068 0 0 0.7071068  330 1"),
            new MapModule("sc_gvg_lingtuzhan_001_6", 1851643953,
                "1024 1024  0.004444445 0 0 0 0 0.004444445 0 0 0 0 -0.01061008 0 0 0 -0.5543767 1  225 75 225  0.7071068 0 0 0.7071068  225 1"),
            new MapModule("sc_gvg_lingtuzhan_001_7", 1851643954,
                "1024 1024  0.004450378 0 0 0 0 0.004450378 0 0 0 0 -0.007587253 0 0 0 -1 1  224.4 114 222.9  0.7071068 0 0 0.7071068  224.7 1"),
            new MapModule("sc_gvg_lingtuzhan_001_8", 1851643947,
                "1024 1024  0.006198838 0 0 0 0 0.006198838 0 0 0 0 -0.01061008 0 0 0 -0.5543767 1  210.1 75 217.48  0.7071068 0 0 0.7071068  161.3206 1"),
            new MapModule("sc_gvg_lingtuzhan_001_9", 1851643948,
                "1024 1024  0.004244533 0 0 0 0 0.004244533 0 0 0 0 -0.01162115 0 0 0 -0.5119117 1  479.2 75 447.2  0.7071068 0 0 0.7071068  235.5972 1"),
            new MapModule("sc_gvg_lingtuzhan_001_10", 1556015652,
                "1024 1024  0.002797045 0 0 0 0 0.002797045 0 0 0 0 -0.02 0 0 0 -3 1  424.1 182.7 427  0.7071068 0 0 0.7071068  357.5202 1"),
            new MapModule("sc_gvg_lingtuzhan_001_11", 2137415359,
                "1024 1024  0.004465558 0 0 0 0 0.004465558 0 0 0 0 -0.01365188 0 0 0 -1 1  233 132.3 225  0.7071068 0 0 0.7071068  223.9362 1"),
            new MapModule("sc_gvg_lingtuzhan_001_12", 571331418,
                "4096 4096  0.002222222 0 0 0 0 0.002222222 0 0 0 0 -0.0005892232 0 0 0 0.233951 1  449.6 0 450.7  0.7071068 0 0 0.7071068  450 1"),
            new MapModule("sc_gvg_lingtuzhan_001_13", 1152731125,
                "1024 1024  0.004444445 0 0 0 0 0.004444445 0 0 0 0 -0.01365188 0 0 0 -1 1  205.8 120 224.7  0.7071068 0 0 0.7071068  225 1"),
            new MapModule("sc_gvg_lingtuzhan_001_14", 1377900472,
                "1024 1024  0.00443459 0 0 0 0 0.00443459 0 0 0 0 -0.009537434 0 0 0 -1 1  231.3 90 224.9  0.7071068 0 0 0.7071068  225.5 1"),
            new MapModule("sc_gvg_lingtuzhan_001_15", 1959300179,
                "1024 1024  0.007194245 0 0 0 0 0.007194245 0 0 0 0 -0.01365188 0 0 0 -1 1  249.8 76.2 209  0.7071068 0 0 0.7071068  139 1"),
            new MapModule("sc_gvg_lingtuzhan_001_16", 393216238,
                "1024 1024  0.004420635 0 0 0 0 0.004420635 0 0 0 0 -0.01365188 0 0 0 -1 1  238.7 142 225.3  0.7071068 0 0 0.7071068  226.2118 1"),
            new MapModule("sc_gvg_lingtuzhan_001_17", 974615945,
                "1024 1024  0.004464275 0 0 0 0 0.004464275 0 0 0 0 -0.01365188 0 0 0 -1 1  467 114 433.9  0.7071068 0 0 0.7071068  224.0005 1"),
            new MapModule("sc_gvg_lingtuzhan_001_18", 1912246012,
                "1024 1024  0.003252032 0 0 0 0 0.003252032 0 0 0 0 -0.01365188 0 0 0 -1 1  432 80 421  0.7071068 0 0 0.7071068  307.5 1"),
            new MapModule("sc_gvg_lingtuzhan_001_19", 346162071,
                "1024 1024  0.002224942 0 0 0 0 0.002224942 0 0 0 0 -0.007796359 0 0 0 -0.1043543 1  450 0 450  0.7071068 0 0 0.7071068  449.45 1"),
            new MapModule("sc_gvg_lingtuzhan_001_20", 1556015653,
                "1024 1024  0.002222222 0 0 0 0 0.002222222 0 0 0 0 -0.01057641 0 0 0 -0.5494447 1  450 126 450  0.7071068 0 0 0.7071068  450 1"),
            new MapModule("sc_gvg_lingtuzhan_001_21", 2137415360,
                "1024 1024  0.004458704 0 0 0 0 0.004458704 0 0 0 0 -0.01057641 0 0 0 -0.5494447 1  238.9 100 224.2  0.7071068 0 0 0.7071068  224.2804 1"),
            new MapModule("sc_gvg_lingtuzhan_001_22", 571331419,
                "1024 1024  0.004943125 0 0 0 0 0.004943125 0 0 0 0 -0.01365188 0 0 0 -1 1  217.1 134 207.9  0.7071068 0 0 0.7071068  202.3012 1"),
            new MapModule("sc_gvg_lingtuzhan_001_23", 1152731126,
                "1024 1024  0.003153579 0 0 0 0 0.003153579 0 0 0 0 -0.01365188 0 0 0 -1 1  438 123 454  0.7071068 0 0 0.7071068  317.1 1"),
            new MapModule("sc_gvg_lingtuzhan_001_24", 1377900473,
                "1024 1024  0.002770083 0 0 0 0 0.002770083 0 0 0 0 -0.01012915 0 0 0 -0.8552545 1  450 128 450  0.7071068 0 0 0.7071068  361 1"),
            new MapModule("sc_gvg_lingtuzhan_001_25", 1959300180,
                "1024 1024  0.005976235 0 0 0 0 0.005976235 0 0 0 0 -0.01365188 0 0 0 -1 1  225 108.4 232.3  0.7071068 0 0 0.7071068  167.3294 1"),
            new MapModule("sc_gvg_lingtuzhan_001_26", 393216239,
                "1024 1024  0.003988831 0 0 0 0 0.003988831 0 0 0 0 -0.005278438 0 0 0 0.1702296 1  463.1 0 450  0.7071068 0 0 0.7071068  250.7 1"),
            new MapModule("sc_gvg_lingtuzhan_001_27", 974615946,
                "1024 1024  0.007163323 0 0 0 0 0.007163323 0 0 0 0 -0.01365188 0 0 0 -1 1  218.5 112.8 245.1  0.7071069 -2.235174e-8 -1.432547e-8 0.7071068  139.6 1"),
            new MapModule("sc_gvg_lingtuzhan_001_28", 1912246013,
                "1024 1024  0.004444445 0 0 0 0 0.004444445 0 0 0 0 -0.008644536 0 0 0 -0.2250173 1  225 74 224.9  0.7071068 0 0 0.7071068  225 1"),
            new MapModule("sc_gvg_lingtuzhan_001_29", 346162072,
                "1024 1024  0.003706449 0 0 0 0 0.003706449 0 0 0 0 -0.01265823 0 0 0 -1 1  465.2 146.4 429  0.7071068 0 0 0.7071068  269.8 1"),
            new MapModule("sc_gvg_lingtuzhan_001_30", 1556015654,
                "1024 1024  0.004444445 0 0 0 0 0.004444445 0 0 0 0 -0.009413093 0 0 0 -0.1405845 1  224 117 225.2  0.7071068 0 0 0.7071068  225 1"),
            new MapModule("sc_gvg_lingtuzhan_001_31", 2137415361,
                "1024 1024  0.003404835 0 0 0 0 0.003404835 0 0 0 0 -0.0008975331 0 0 0 -0.7762449 1  454.4 868 436.7  0.7071068 0 0 0.7071068  293.7 1"),
            new MapModule("sc_gvg_lingtuzhan_001_32", 571331420,
                "1024 1024  0.004545454 0 0 0 0 0.004545454 0 0 0 0 -0.01089918 0 0 0 0.8092643 1  207.8 0 224.9  0.7071068 0 0 0.7071068  220 1"),
            new MapModule("sc_gvg_lingtuzhan_001_33", 1152731127,
                "1024 1024  0.004035297 0 0 0 0 0.004035297 0 0 0 0 -0.01089918 0 0 0 0.8092643 1  406 -139 468  0.7071068 0 0 0.7071068  247.8132 1"),
            new MapModule("sc_gvg_lingtuzhan_001_34", 1377900474,
                "1024 1024  0.005043269 0 0 0 0 0.005043269 0 0 0 0 -0.01840943 0 0 0 -0.3586156 1  225 50.5 236.1  0.7071068 0 0 0.7071068  198.2841 1"),
            new MapModule("sc_gvg_lingtuzhan_001_35", 1959300181,
                "1024 1024  0.003192848 0 0 0 0 0.003192848 0 0 0 0 -0.005918911 0 0 0 1 1  435 -139 450  0.7071068 0 0 0.7071068  313.2 1"),
            new MapModule("sc_gvg_lingtuzhan_001_36", 393216240,
                "1024 1024  0.00339213 0 0 0 0 0.00339213 0 0 0 0 -0.002020957 0 0 0 0.5211746 1  462 0 420  0.7071068 0 0 0.7071068  294.8 1"),
        };

        private static object _doneEnv;

        /// <summary>From LuaPatches.Tick: once per LuaEnv, as soon as package.loaded exists (long before any scene load).</summary>
        public static void Install(object env, Obf_R.Obf_ff global)
        {
            if (ReferenceEquals(env, _doneEnv) || global == null) return;
            try
            {
                var loaded = global.Obf_LuA<Obf_R.Obf_ff>("package.loaded");
                var pack = global.Obf_LuA<Obf_R.Obf_Ef>("table.pack");
                var rawset = global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
                if (loaded == null || pack == null || rawset == null) return;
                _doneEnv = env;
                var added = new List<string>();
                foreach (var module in Modules)
                {
                    // A module the build does ship (or one already put here) is left alone.
                    if (loaded.Obf_RuA<object>(module.Name) != null) continue;
                    loaded.Obf_luA<string, object>(module.Name, Build(module, pack, rawset));
                    added.Add(module.Name);
                }
                Debug.Log("[Ro3Emu.MapModules] " + added.Count + " map module(s) the build does not ship added to package.loaded"
                    + (added.Count > 0 ? ": " + string.Join(", ", added.ToArray()) : ""));
            }
            catch (Exception e)
            {
                _doneEnv = env; // one attempt per LuaEnv, as LuaPatches
                Debug.LogWarning("[Ro3Emu.MapModules] " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The module table as the original chunk returns it (Config/Map/sc_gef_dun02_001.lua has the same shape).</summary>
        private static Obf_R.Obf_ff Build(MapModule module, Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset)
        {
            var table = NewTable(pack, rawset);
            table.Obf_luA<string, object>("kMapId", module.MapId);
            table.Obf_luA<string, object>("kNavMeshExporter", "map/bin/" + module.Name + ".bin");
            table.Obf_luA<string, object>("ItemName", module.Name);
            var cameras = NewTable(pack, rawset);
            for (int i = 0; i < module.Cameras.Length; i++)
                cameras.Obf_luA<long, object>(i, Camera(module.Name, module.Cameras[i], pack, rawset));
            table.Obf_luA<string, object>("MiniMapInfo", cameras);
            return table;
        }

        private static Obf_R.Obf_ff Camera(string name, string text, Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset)
        {
            var v = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (v.Length != 27) throw new FormatException(name + ": a camera has " + v.Length + " numbers, not 27");
            var camera = NewTable(pack, rawset);
            camera.Obf_luA<string, object>("CamPixelWidth", Number(v[0]));
            camera.Obf_luA<string, object>("CamPixelHeight", Number(v[1]));
            var p = NewTable(pack, rawset);
            for (int i = 0; i < 16; i++) p.Obf_luA<long, object>(i, Number(v[2 + i]));
            camera.Obf_luA<string, object>("P", p);
            camera.Obf_luA<string, object>("CameraPos", Fields(pack, rawset, "x", v[18], "y", v[19], "z", v[20]));
            camera.Obf_luA<string, object>("CameraRotation", Fields(pack, rawset, "x", v[21], "y", v[22], "z", v[23], "w", v[24]));
            camera.Obf_luA<string, object>("CameraOrthographicSize", Number(v[25]));
            camera.Obf_luA<string, object>("CameraAspect", Number(v[26]));
            return camera;
        }

        private static Obf_R.Obf_ff Fields(Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset, params string[] pairs)
        {
            var table = NewTable(pack, rawset);
            for (int i = 0; i + 1 < pairs.Length; i += 2) table.Obf_luA<string, object>(pairs[i], Number(pairs[i + 1]));
            return table;
        }

        /// <summary>A Lua integer (long) or float (double), as the original constant was.</summary>
        private static object Number(string text) =>
            text.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0
                ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : (object)long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

        /// <summary>A new Lua table: table.pack() without its count fields (n, and this client's __paramslen).</summary>
        private static Obf_R.Obf_ff NewTable(Obf_R.Obf_Ef pack, Obf_R.Obf_Ef rawset)
        {
            var table = (Obf_R.Obf_ff)pack.Obf_HuA()[0];
            rawset.Obf_HuA(table, "n", null);
            rawset.Obf_HuA(table, "__paramslen", null);
            return table;
        }
    }
}
