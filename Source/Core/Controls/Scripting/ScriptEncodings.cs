#region ================== Namespaces

using System;
using System.Text;

#endregion

namespace CodeImp.DoomBuilder.Controls
{
	// Text encodings supported by the script editor: detection on load, decoding and encoding of script data
	public static class ScriptEncodings
	{
		#region ================== Constants

		public const int UTF8 = 65001;
		public const int WINDOWS1251 = 1251;

		// Encodings offered in the script editor and in the preferences, in display order
		public static readonly int[] CodePages = { UTF8, WINDOWS1251 };

		private static readonly byte[] UTF8_BOM = { 0xEF, 0xBB, 0xBF };

		#endregion

		#region ================== Variables

		private static readonly Encoding utf8 = new UTF8Encoding(false);
		private static readonly Encoding utf8strict = new UTF8Encoding(false, true);

		#endregion

		#region ================== Properties

		// Encoding for new scripts and for scripts whose encoding can't be detected
		public static int DefaultCodePage { get { return IsSupported(General.Settings.ScriptDefaultEncoding) ? General.Settings.ScriptDefaultEncoding : UTF8; } }

		// Legacy 8-bit encoding used when auto-detection finds the data is not valid UTF-8
		public static int FallbackCodePage { get { return (DefaultCodePage == UTF8 ? WINDOWS1251 : DefaultCodePage); } }

		#endregion

		#region ================== Methods

		public static bool IsSupported(int codepage)
		{
			return Array.IndexOf(CodePages, codepage) != -1;
		}

		public static string GetDisplayName(int codepage)
		{
			switch(codepage)
			{
				case UTF8: return "UTF-8";
				case WINDOWS1251: return "Windows-1251 (Cyrillic)";
				default: return "Code page " + codepage;
			}
		}

		// Short name for the status bar
		public static string GetShortName(int codepage, bool bom)
		{
			switch(codepage)
			{
				case UTF8: return (bom ? "UTF-8 BOM" : "UTF-8");
				case WINDOWS1251: return "Windows-1251";
				default: return "CP" + codepage;
			}
		}

		public static Encoding GetEncoding(int codepage)
		{
			return (codepage == UTF8 ? utf8 : Encoding.GetEncoding(codepage));
		}

		public static bool HasUtf8Bom(byte[] data)
		{
			return data != null && data.Length >= 3 && data[0] == UTF8_BOM[0] && data[1] == UTF8_BOM[1] && data[2] == UTF8_BOM[2];
		}

		// Detects the encoding of script data.
		// A UTF-8 BOM always means UTF-8. With auto-detection enabled, data that contains non-ASCII bytes
		// and is valid UTF-8 is UTF-8, any other non-ASCII data uses the fallback 8-bit code page.
		// Pure ASCII data and data loaded with auto-detection disabled use the default code page.
		public static int Detect(byte[] data, out bool bom)
		{
			bom = HasUtf8Bom(data);
			if(bom) return UTF8;
			if(!General.Settings.ScriptAutoDetectEncoding || data == null || IsAscii(data)) return DefaultCodePage;
			return (IsValidUtf8(data) ? UTF8 : FallbackCodePage);
		}

		// Decodes script data in the given code page (a UTF-8 BOM is skipped)
		public static string Decode(byte[] data, int codepage)
		{
			if(data == null || data.Length == 0) return string.Empty;
			int offset = (codepage == UTF8 && HasUtf8Bom(data) ? UTF8_BOM.Length : 0);
			return GetEncoding(codepage).GetString(data, offset, data.Length - offset);
		}

		// Decodes script data in its detected encoding (used where data is read without an editor, e.g. text search)
		public static string Decode(byte[] data)
		{
			bool bom;
			return Decode(data, Detect(data, out bom));
		}

		public static byte[] Encode(string text, int codepage, bool bom)
		{
			byte[] data = GetEncoding(codepage).GetBytes(text ?? string.Empty);
			if(codepage != UTF8 || !bom) return data;

			byte[] result = new byte[data.Length + UTF8_BOM.Length];
			Buffer.BlockCopy(UTF8_BOM, 0, result, 0, UTF8_BOM.Length);
			Buffer.BlockCopy(data, 0, result, UTF8_BOM.Length, data.Length);
			return result;
		}

		// Returns the number of characters that can't be represented in the given code page
		// (they would be replaced when saving)
		public static int CountUnencodable(string text, int codepage)
		{
			if(codepage == UTF8 || string.IsNullOrEmpty(text)) return 0;

			Encoding strict = Encoding.GetEncoding(codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
			try
			{
				strict.GetByteCount(text);
				return 0;
			}
			catch(EncoderFallbackException) { }

			// Slow path: count the offending characters one by one
			int count = 0;
			for(int i = 0; i < text.Length; i++)
			{
				int len = (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1);
				try { strict.GetByteCount(text.Substring(i, len)); }
				catch(EncoderFallbackException) { count++; }
				i += len - 1;
			}

			return count;
		}

		private static bool IsAscii(byte[] data)
		{
			foreach(byte b in data) if(b > 0x7F) return false;
			return true;
		}

		private static bool IsValidUtf8(byte[] data)
		{
			try
			{
				utf8strict.GetCharCount(data);
				return true;
			}
			catch(DecoderFallbackException)
			{
				return false;
			}
		}

		#endregion
	}
}
