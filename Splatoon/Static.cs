﻿global using Dalamud.Game.ClientState.Objects.Types;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.Numerics;
global using Dalamud.Plugin;
global using ImGuiNET;
global using System.Runtime.ExceptionServices;
global using System.Collections.Concurrent;
global using Dalamud.Logging;
global using static Splatoon.Static;
global using Dalamud.Interface;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Text;
global using System.Threading.Tasks;

namespace Splatoon
{
    static class Static
    {
        public static void Toggle<T>(this HashSet<T> h, T o)
        {
            if(h.Contains(o))
            {
                h.Remove(o);
            }
            else
            {
                h.Add(o);
            }
        }

        public static bool EqualsIgnoreCase(this string a, string b)
        {
            return a.Equals(b, StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool StartsWithIgnoreCase(this string a, string b)
        {
            return a.StartsWith(b, StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool ContainsIgnoreCase(this string a, string b)
        {
            return a.ToLower(CultureInfo.InvariantCulture).Contains(b.ToLower(CultureInfo.InvariantCulture));
        }

        public static string Compress(this string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s);
            using (var msi = new MemoryStream(bytes))
            using (var mso = new MemoryStream())
            {
                using (var gs = new GZipStream(mso, CompressionLevel.Optimal))
                {
                    msi.CopyTo(gs);
                }
                return Convert.ToBase64String(mso.ToArray()).Replace('+', '-').Replace('/', '_');
            }
        }

        public static string ToBase64UrlSafe(this string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).Replace('+', '-').Replace('/', '_');
        }

        public static string FromBase64UrlSafe(this string s)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/')));
        }

        public static string Decompress(this string s)
        {
            var bytes = Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/'));
            using (var msi = new MemoryStream(bytes))
            using (var mso = new MemoryStream())
            {
                using (var gs = new GZipStream(msi, CompressionMode.Decompress))
                {
                    gs.CopyTo(mso);
                }
                return Encoding.Unicode.GetString(mso.ToArray());
            }
        }



        //because Dalamud changed Y and Z in actor positions I have to do emulate old behavior to not break old presets
        public static Vector3 GetPlayerPositionXZY()
        {
            if (Svc.ClientState.LocalPlayer != null)
                return new Vector3(Svc.ClientState.LocalPlayer.Position.X,
                    Svc.ClientState.LocalPlayer.Position.Z,
                    Svc.ClientState.LocalPlayer.Position.Y);
            return new Vector3() { X = 0, Y = 0, Z = 0 };
        }

        public static Vector3 GetPositionXZY(this GameObject a)
        {
            return new Vector3(a.Position.X,
                    a.Position.Z,
                    a.Position.Y);
        }
    }
}