using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static XPdf.ArithmDecoder;

namespace XPdf {
	internal class ArithmDecoder {
		internal class CXStats {
			internal byte[] cxTab;
			internal CXStats(int contextSizeA = 512) {
				cxTab = new byte[contextSizeA];
				Reset();
			}
			internal void Reset() { Array.Clear(cxTab, 0, cxTab.Length); }
			internal void SetEntry(uint cx, int i, int mps) {
				cxTab[cx] = (byte)((i << 1) + mps);
			}
		};
		static readonly uint[] qeTab = {
			0x56010000, 0x34010000, 0x18010000, 0x0AC10000, 0x05210000, 0x02210000, 0x56010000, 0x54010000,
			0x48010000, 0x38010000, 0x30010000, 0x24010000, 0x1C010000, 0x16010000, 0x56010000, 0x54010000,
			0x51010000, 0x48010000, 0x38010000, 0x34010000, 0x30010000, 0x28010000, 0x24010000, 0x22010000,
			0x1C010000, 0x18010000, 0x16010000, 0x14010000, 0x12010000, 0x11010000, 0x0AC10000, 0x09C10000,
			0x08A10000, 0x05210000, 0x04410000, 0x02A10000, 0x02210000, 0x01410000, 0x01110000, 0x00850000,
			0x00490000, 0x00250000, 0x00150000, 0x00090000, 0x00050000, 0x00010000, 0x56010000
		};
		static readonly int[] nmpsTab = {
			1,  2,  3,  4,  5, 38,  7,  8,  9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
			25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
		};
		static readonly int[] nlpsTab = {
			1,  6,  9, 12, 29, 33,  6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
			22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
		};
		static readonly int[] switchTab = {
			1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
		};
		internal ArithmDecoder(MemoryStream str = null) {
			if (str != null) {
				SetStream(str);
				Start();
			}
		}
		internal void SetStream(MemoryStream strA) { str = strA; dataLen = 0; limitStream = false; }
		internal void SetStream(MemoryStream strA, int dataLenA) { str = strA; dataLen = dataLenA; limitStream = true; }
		// Start decoding on a new stream.  This fills the byte buffers and runs INITDEC.
		internal void Start() {
			(buf0, buf1) = (ReadByte(), ReadByte());
			c = (buf0 ^ 0xff) << 16;    // INITDEC
			ByteIn();
			c <<= 7;
			ct -= 7;
			a = 0x80000000;
		}
		// Restart decoding on an interrupted stream.  This refills the
		// buffers if needed, but does not run INITDEC.  (This is used in
		// JPEG 2000 streams when codeblock data is split across multiple
		// packets/layers.)
		internal void Restart(int dataLenA) {
			if (dataLen >= 0)
				dataLen = dataLenA;
			else if (dataLen == -1) {
				dataLen = dataLenA;
				buf1 = ReadByte();
			}
			else {
				int k = (-dataLen - 1) * 8 - ct, nBits;
				dataLen = dataLenA;
				uint cAdd = 0;
				for (bool prevFF = false; k > 0; prevFF = buf0 == 0xff) {
					buf0 = ReadByte();
					if (prevFF) {
						cAdd += 0xfe00 - (buf0 << 9);
						nBits = 7;
					}
					else {
						cAdd += 0xff00 - (buf0 << 8);
						nBits = 8;
					}
					if (k > nBits) {
						cAdd <<= nBits;
						k -= nBits;
					}
					else {
						cAdd <<= k;
						ct = nBits - k;
						k = 0;
					}
				}
				c += cAdd;
				buf1 = ReadByte();
			}
		}
		// Read any leftover data in the stream.
		internal void Cleanup() {
			if (!limitStream) return;
			// This saves one extra byte of data from the end of packet i, to be used in packet i+1.
			// It's not clear from the JPEG 2000 spec exactly how this should work, but this kludge
			// does seem to fix decode of some problematic JPEG 2000 streams.  It may actually be
			// necessary to buffer an arbitrary number of bytes (> 1), but I haven't run into that case yet.
			for (; dataLen > 0; readBuf = (int)ReadByte())
				readBuf = -1;
		}
		internal int DecodeBit(int context, CXStats stats) {
			int ctx = stats.cxTab[context], iCX = ctx >> 1, mps = ctx & 1, bit;
			uint qe = qeTab[iCX];
			bool chk;
			a -= qe;
			if (c < a) {
				if (0 != (a & 0x80000000))
					return mps;
				chk = a < qe;
			}
			else {
				c -= a;                         // LPS_EXCHANGE
				chk = a >= qe;
				a = qe;                         // RENORMD
			}
			if (chk) {
				bit = 1 - mps;
				stats.cxTab[context] = (byte)((nlpsTab[iCX] << 1)
							| (0 != switchTab[iCX] ? bit : mps));
			}
			else {
				bit = mps;
				stats.cxTab[context] = (byte)((nmpsTab[iCX] << 1) | bit);
			}
			do {
				if (ct == 0) ByteIn();
				a <<= 1; c <<= 1; --ct;
			} while (0 == (a & 0x80000000));
			return bit;
		}
		// Returns false for OOB, otherwise sets *<x> and returns true.
		internal long DecodeInt(CXStats stats) {
			prev = 1;
			int s = DecodeIntBit(stats) == 0 ? 1 : -1, v, i, a;
			if (0 != DecodeIntBit(stats))
				if (0 != DecodeIntBit(stats))
					if (0 != DecodeIntBit(stats))
						if (0 != DecodeIntBit(stats))
							(i, a) = (0 != DecodeIntBit(stats))
								? (32, 4436) : (12, 340);
						else
							(i, a) = (8, 84);
					else
						(i, a) = (6, 20);
				else
					(i, a) = (4, 4);
			else
				(i, a) = (2, 0);
			for (v = 0; i > 0; --i)
				v = (v << 1) | DecodeIntBit(stats);
			v += a;
			if (s != 1 && v == 0)
				return long.MaxValue;
			return s * v;
		}
		internal uint DecodeIAID(int codeLen, CXStats stats) {
			prev = 1;
			for (int i = 0; i < codeLen; ++i) {
				int bit = DecodeBit((int)prev, stats);
				prev = (uint)(((int)prev << 1) | bit);
			}
			return (uint)(prev - (1 << codeLen));
		}
		uint ReadByte() {
			if (limitStream) {
				if (readBuf >= 0) {
					uint x = (uint)readBuf;	
					readBuf = -1;
					return x;
				}
				if (--dataLen < 0)
					return 0xff;
			}
			return (uint)str.ReadByte() & 0xff;
		}
		int DecodeIntBit(CXStats stats) {
			int bit = DecodeBit((int)prev, stats);
			prev = (uint)((prev < 0x100) ? ((int)prev << 1) | bit
						: ((((int)prev << 1) | bit) & 0x1ff) | 0x100);
			return bit;
		}
		void ByteIn() {
			if (buf0 != 0xff) {
				(buf0, buf1, ct) = (buf1, ReadByte(), 8);
				c += 0xff00 - (buf0 << 8);
			}
			else if (buf1 <= 0x8f) {
				(buf0, buf1, ct) = (buf1, ReadByte(), 7);
				c += 0xfe00 - (buf0 << 9);
			}
			else {
				if (limitStream) {
					(buf0, buf1) = (buf1, ReadByte());
					c += 0xff00 - (buf0 << 8);
				}
				ct = 8;
			}
		}
		uint buf0, buf1, c, a, prev;         // for the integer decoder
		int ct, dataLen = 0, readBuf = -1;
		MemoryStream str = null;
		bool limitStream = false;
	};
	internal class ImgStream : MemoryStream {
		long bitBuf = 0, bitAvl = 0;
		public ImgStream(byte[] data, int off = 0, int len = -1) 
				: base(data, off, len < 0 ? data.Length - off : len, false, true) {
		}
		public ImgStream(ImgStream oth, long off, long len)
				: base(oth.GetBuffer(), (int)off, (int)len, false, true) {
		}
		public sbyte ReadSByte() {
			bitAvl = 0;
			int ch = ReadByte();
			if (ch < 0) throw new EndOfStreamException();
			return (sbyte)ch;
		}
		public int ReadInt(int n = 4) {
			if (EOF(n - 1))
				return -1; // throw new EndOfStreamException();
			bitAvl = 0;
			int ret = 0;
			for (; n > 0; n--)
				ret = ret << 8 | ReadByte();
			return ret;
		}
		public int ReadBits(int numBits) {
			if (numBits == 0) return 0;
			for (; bitAvl < numBits; bitAvl += 8)
				bitBuf = bitBuf << 8 | (long)ReadByte();
			var ret = ((ulong)bitBuf >> (int)(bitAvl - numBits)) & (ulong)((1 << numBits) - 1);
			bitAvl -= numBits;
			return (int)ret;
		}
		public override long Seek(long pos, SeekOrigin loc) {
			bitAvl = 0;
			return base.Seek(pos, loc);
		}
		public bool EOF(int n = 0) {
			return Position + n >= Length;
		}
	}
	public class XPdfStream {
		internal ImgStream bufStr = null;      // buffered stream (for lookahead)
		internal int GetInt(int nBytes) {
			return bufStr.ReadInt(nBytes);
		}
		internal static bool Error(string msg, bool fatal = true) {
			if (fatal) throw new Exception(msg);
#if SA_TEST
			else Console.WriteLine(msg);
#else
			else WebLib.Utils.Log(msg + "\n" + Environment.StackTrace);
#endif
			return false;
		}
	}
	public class XPdfJpx : XPdfStream {
		class JPXPalette {
			internal int nEntries;          // number of entries in the palette
			internal int nComps;           // number of components in each entry
			internal uint[] bpc = null;     // bits per component, for each component
			internal int[] c = null;        // color data: c[i*nComps+j] = entry i, component j
		};
		class JPXTagTreeNode {
			internal bool	finished = false;   // true if this node is finished
			internal int	val = 0;            // current value
		};
		class JPXCodeBlock {                    //----- size
			internal int x0, y0, x1, y1;        // bounds
			internal bool seen = false;         // true if this code-block has already been seen
			internal int lBlock;                // base number of bits used for pkt data length
			internal int nextPass;              // next coding pass
			internal int nBitPlanes0;			// number of zero bit planes
			internal int included;              // code-block inclusion in this packet: 0=not included, 1=included
			internal int nCodingPasses;         // number of coding passes in this pkt
			internal int[] dataLen = null;      // data lengths (one per codeword segment)
												//----- coefficient data
			internal int[]	coeffs;
			internal int	coefOff;
			internal bool[] touched = null;		// coefficient 'touched' flags
			internal int	len;				// coefficient length
			internal ArithmDecoder arithDecoder = null;// arithmetic decoder
			internal CXStats stats = null;		// arithmetic decoder stats
		};
		class JPXSubband {
			internal int nXCBs, nYCBs;              // number of code-blocks in the x and y directions
			internal int maxTTLevel;                // max tag tree level
			internal JPXTagTreeNode[] inclusion;    // inclusion tag tree for each subband
			internal JPXTagTreeNode[] BitPlane0;	// zero-bit plane tag tree for each subband
			internal JPXCodeBlock[] cbs;            //----- children the code-blocks (len = nXCBs * nYCBs)
		};
		class JPXPrecinct {
			internal JPXSubband[] subbands = null;  //----- children the subbands
		};
		class JPXResLevel {
			//----- from the COD and COC segments (main and tile)
			internal int precinctWidth = 15, precinctHeight = 15;  // log2(precinct width/height)
			//----- computed
			internal int x0, y0, x1, y1;                        // bounds of this tile-comp at this res level
			internal int[] bx0 = new int[3], by0 = new int[3],  // subband bounds
						   bx1 = new int[3], by1 = new int[3];
			internal int codeBlockW, codeBlockH;                // log2(code-block width/height)
			internal int cbW, cbH;                              // code-block width/height
			internal bool empty;            // true if all subbands and precincts are zero width or height
			internal JPXPrecinct[] precincts = null;			//---- children the precincts
		};
		class JPXTileComp {
			//----- from the SIZ segment
			internal bool sgned;					// 1 for signed, 0 for unsigned
			internal int prec;                      // precision, in bits
			internal int hSep, vSep;                // hor/vert separation of samples

			//----- from the COD and COC segments (main and tile)
			internal int style;                     // coding style parameter (Scod / Scoc)
			internal int nDecompLevels;             // number of decomposition levels
			internal int codeBlockW, codeBlockH;    // log2(code-block width/height)
			internal int codeBlockStyle;            // code-block style
			internal int transform;                 // wavelet transformation
													//----- from the QCD and QCC segments (main and tile)
			internal int quantStyle;                // quantization style
			internal int[] quantSteps = null;       // quantization step size for each subband
			internal int x0, y0, x1, y1;            // bounds of the tile-comp, in ref coords
			internal int w, h;                      // data size = {x1 - x0, y1 - y0} >> reduction
													//----- image data
			internal int[] data = null;             // the decoded image data
			internal int[] buf = null;              // intermediate buffer for the inverse transform
			internal JPXResLevel[] resLevels = null; //----- children the resolution levels (len = nDecompLevels + 1)
			internal JPXTileComp Clone() {
				return MemberwiseClone() as JPXTileComp;
			}
			internal void Init(JPXTileComp tmpTile) {
				if (tmpTile.nDecompLevels < 1 || tmpTile.nDecompLevels > 31
				||  tmpTile.codeBlockW > 10 || tmpTile.codeBlockH > 10
				||  tmpTile.transform == -1)
					Error("Error in JPX COD marker segment");
				style = tmpTile.style;
				nDecompLevels = tmpTile.nDecompLevels;
				codeBlockW = tmpTile.codeBlockW;
				codeBlockH = tmpTile.codeBlockH;
				codeBlockStyle = tmpTile.codeBlockStyle;
				transform = tmpTile.transform;
				ReAlloc(ref resLevels, tmpTile.nDecompLevels + 1);
				for (int r = 0; r <= tmpTile.nDecompLevels; ++r)
					resLevels[r].precincts = null;
			}
			internal void InitResLvels(XPdfJpx jpx) {
				for (int r = 0, sz; r <= nDecompLevels; ++r) 
					if (0 != (style & 0x01)) {
						if ((sz = jpx.bufStr.ReadInt(1)) == -1)
							Error("Error in JPX COD marker segment");
						if (r > 0 && ((sz & 0x0f) == 0 || (sz & 0xf0) == 0))
							Error("Invalid precinct size in JPX COD marker segment");
						resLevels[r].precinctWidth = sz & 0x0f;
						resLevels[r].precinctHeight = (sz >> 4) & 0x0f;
					}
					else 
						resLevels[r].precinctWidth = resLevels[r].precinctHeight = 15;
			}
			internal void InitQuantSteps(XPdfJpx jpx, int segLen) {
				int nQuantSteps = 0;
				if ((quantStyle & 0x1f) == 0x00)
					nQuantSteps = segLen;
				else if ((quantStyle & 0x1f) == 0x01)
					nQuantSteps = 1;
				else if ((quantStyle & 0x1f) == 0x02)
					nQuantSteps = segLen / 2;
				if (nQuantSteps < 1)
					Error("Error in JPX QCD marker segment");
				ReAlloc(ref quantSteps, nQuantSteps);
				for (int i = 0; i < nQuantSteps; ++i)
					if ((quantSteps[i] = jpx.bufStr.ReadInt(1)) == -1)
						Error("Error in JPX QCD marker segment");
			}
		};
		class JPXTile {
			internal bool init = false;
			//----- from the COD segments (main and tile)
			internal int progOrder;				// progression order
			internal int nLayers;				// number of layers
			internal int multiComp;				// multiple component transformation
			//----- computed
			internal int x0, y0, x1, y1;		// bounds of the tile, in ref coords
			internal int maxNDecompLvls;		// max number of decomposition levels used in any component in this tile
			internal int maxNPrecincts;			// max number of precints in any component/res level in this tile
			//----- progression order loop counters
			internal int comp;					// component
			internal int res;					// resolution level
			internal int precinct;				// precinct
			internal int layer;					// layer
			internal bool done;					// set when this tile is done
			internal int nextTilePart = 0;		// next expected tile-part
			internal JPXTileComp[] tileComps = null;// the tile-components (len = JPXImage.nComps)
		};
		class JPXImage {                        //----- from the SIZ segment
			internal int xSize,		ySize;      // size of reference grid
			internal int xOffset,	yOffset;    // image offset
			internal int xTileSize, yTileSize;  // size of tiles
			internal int xTileOff,	yTileOff;	// offset of first tile
			internal int nXTiles,	nYTiles;    // number of tiles in x/y direction
			internal int nComps;                // number of components
			internal JPXTile[] tiles = null;    // the tiles (len = nXTiles * nYTiles)
		};

		const int jpxNContexts = 19, jpxContextSigProp = 0, jpxContextRunLen = 17, jpxContextUniform = 18;
		const int jpxPassSigProp = 0, jpxPassMagRef = 1, jpxPassCleanup = 2;

		//------------------------------------------------------------------------
		const int sigPropCtxH = 15, sigPropCtxV = 5; // 3 hor, 3 ver, 5 diag
		static readonly int[][] sigPropCtx = new int[][] {
			new int[] {0,1,2,2,2, 5,6,6,6,6, 8,8,8,8,8, 3,3,3,3,3, 7,7,7,7,7, 8,8,8,8,8, 4,4,4,4,4, 7,7,7,7,7, 8,8,8,8,8 },
			new int[] {0,1,2,2,2, 3,3,3,3,3, 4,4,4,4,4, 5,6,6,6,6, 7,7,7,7,7, 7,7,7,7,7, 8,8,8,8,8, 8,8,8,8,8, 8,8,8,8,8 },
			new int[] {0,3,6,8,8, 1,4,7,8,8, 2,5,7,8,8, 1,4,7,8,8, 2,5,7,8,8, 2,5,7,8,8, 2,5,7,8,8, 2,5,7,8,8, 2,5,7,8,8 },
		};
		// arithmetic decoder context and xor bit for the sign bit in the significance propagation pass:
		//     [horiz*5+vert] & (k << 4)
		// where horiz/vert are offset by 2 (i.e., range is -2 .. 2)
		// and k = 0 for the context
		//       = 1 for the xor bit
		static readonly int[] signContext = new int[] {
			0x1d,0x1d,0x1c,0x1b,0x1b, 0x1d,0x1d,0x1c,0x1b,0x1b, 0x1a,0x1a,0x09,0x0a,0x0a,
			0x0b,0x0b,0x0c,0x0d,0x0d, 0x0b,0x0b,0x0c,0x0d,0x0d
		};
		// sum of the sample size (number of bits) and the number of bits to
		// the right of the decimal point for the fixed point arithmetic used
		// in the IDWT
		const int fracBits = 24;
		int jpxFloorDivPow2(int x, int y) { return x <= 0 ? 0 : x >> y; }
		int jpxCeilDiv(int x, int y) { return (x + y - 1) / y; }
		int jpxCeilDivPow2(int x, int y) { return (x + (1 << y) - 1) >> y; }
		public XPdfJpx(byte[] strA) {
			bufStr = new ImgStream(strA);
		}
		public Image decodeImage(byte[] gsPlt = null) {
			bufStr.Position = 0;
			ReadBoxes();
			int nComps = (havePalette ? palette.nComps : img.nComps);
			if (nComps != 3 && nComps != 1) throw new Exception("invalid format");
			Bitmap bmp = new Bitmap(img.xSize, img.ySize, PixelFormat.Format24bppRgb);
			var bd = bmp.LockBits(new Rectangle(0, 0, img.xSize, img.ySize),
											ImageLockMode.ReadWrite, bmp.PixelFormat);
			byte[] buf = new byte[bd.Stride];
			for (int curY = img.yOffset; curY < img.ySize; curY++) {
				Array.Clear(buf, 0, buf.Length);
				for (int curX = img.xOffset; curX < img.xSize; curX++) {
					var tile = img.tiles[((curY - img.yTileOff) / img.yTileSize * img.nXTiles
										+ (curX - img.xTileOff) / img.xTileSize)];
					JPXTileComp tileComp = tile.tileComps[0];
					int idx = Math.Max(0, curY / tileComp.vSep - tileComp.y0) * tileComp.w
							+ Math.Max(0, curX / tileComp.hSep - tileComp.x0);
					for (int curComp = 0; curComp < nComps; curComp++) {
						int pix = tile.tileComps[havePalette ? 0 : curComp].data[idx];
						if (havePalette)
							pix = (pix >= 0 && pix < palette.nEntries)
								? palette.c[pix * palette.nComps + curComp] : 0;
						if (nComps != 1)
							buf[curX * nComps + (2 - curComp)] = (byte)pix;
						else if (pix != 0) {
							pix = gsPlt == null ? 255   // pix ?
									: ((gsPlt.Length > 256) ? gsPlt[pix * 3] : gsPlt[pix]);
							buf[curX*3] = buf[curX*3+1] = buf[curX*3+2] = (byte)pix;
						}
					}   // still some work to do grayscale are not quite working...
				}
				Marshal.Copy(buf, 0, bd.Scan0 + curY * bd.Stride, buf.Length);
			}
			img = null;
			bmp.UnlockBits(bd);
			return bmp;
		}
		static T[] Alloc<T>(int num) where T : new() {
			T[] r = new T[num];
			for (int i = 0; i < num; i++)
				r[i] = new T();
			return r;
		}
		static T[] ReAlloc<T>(ref T[] a, int n) where T : new() {
			int o = a == null ? 0 : a.Length;
			if (a == null)
				a = new T[n];
			else if (o != n)
				Array.Resize(ref a, n);
			for (int i = o; i < n; i++)
				a[i] = new T();
			return a;
		}
		void ReadBoxes() {
			int boxType = 0, boxLen = 0, dataLen = 0, compression;
			if (new int[] { 0x0c, 0x6a502020, 0x0d0a870a }.Any(x => x != bufStr.ReadInt(4)))
				Error("File is neither valid JP2 file nor valid JPEG 2000 codestream");
			var dataStart = bufStr.Position;
			for (; ReadBoxHdr(ref boxType, ref boxLen, ref dataLen); bufStr.Position = dataStart + dataLen) {
				dataStart = bufStr.Position;
				switch (boxType) {
					case 0x69686472:                // image header
						bufStr.Position += 8; nComps = bufStr.ReadInt(2);
						bufStr.Position += 1; compression = bufStr.ReadInt(1);
						if (compression != 7 || nComps < 1 || bufStr.EOF())
							Error("Bad header info");
						break;
					case 0x70636c72:        // palette
						if ((palette.nEntries = GetInt(2)) == -1 || (palette.nComps = GetInt(1)) == -1)
							Error("Unexpected EOF in JPX stream");
						havePalette = true;
						palette.bpc = new uint[palette.nComps];
						palette.c = new int[palette.nEntries * palette.nComps];
						for (int i = 0; i < palette.nComps; ++i)
							palette.bpc[i] = (uint)bufStr.ReadInt(1) + 1;
						for (int i = 0; i < palette.nEntries; ++i)
							for (int j = 0; j < palette.nComps; ++j)
								palette.c[i * palette.nComps + j] = GetInt((((int)palette.bpc[j] & 0x7f) + 7) >> 3);
						break;
					case 0x6A703263:        // contiguous codestream
						ReadCodestream();
						dataStart = bufStr.Position; dataLen = 0;
						break;
					case 0x6a703268:        // JP2 header
											// this is a grouping box ('superbox') which has no real contents and
											// doesn't appear to be used consistently, i.e., some things which should
											// be subboxes of the JP2 header box show up outside of it - so we simply
											// ignore the JP2 header box
					case 0x63646566:        // channel definition
					case 0x636d6170:        // component mapping
					case 0x62706363:        // bits per component
					case 0x636F6C72:        // color specification
					default:
						break;
				}
			}
		}
		void ReadCodestream() {
			bool haveSIZ = false, haveCOD = false, haveQCD = false, haveSOT = false;
			int segType = 0, progOrder, multiComp, style, nLayers, segLen = 0, capabilities, comp, i, r;
			JPXTileComp tmpTile;
			do {                //----- main header
				ReadMarkerHdr(ref segType, ref segLen);
				switch (segType) {
					case 0x4f: break;   // SOC - start of codestream
					case 0x51:          // SIZ - image and tile size
						capabilities = bufStr.ReadInt(2);
						(img.xSize, img.ySize, img.xOffset, img.yOffset) = (GetInt(4), GetInt(4), GetInt(4), GetInt(4));
						(img.xTileSize, img.yTileSize, img.xTileOff, img.yTileOff) = (GetInt(4), GetInt(4), GetInt(4), GetInt(4));
						if (haveSIZ || (img.nComps = GetInt(2)) == -1 || nComps != 0 && img.nComps != nComps
						|| img.xSize == 0 || img.ySize == 0
						|| img.xOffset >= img.xSize || img.yOffset >= img.ySize
						|| img.xTileSize == 0 || img.yTileSize == 0
						|| img.xTileOff > img.xOffset || img.yTileOff > img.yOffset
						|| img.xTileSize + img.xTileOff <= img.xOffset
						|| img.yTileSize + img.yTileOff <= img.yOffset || img.nComps == 0)
							Error("Error in JPX SIZ marker segment");
						img.nXTiles = (img.xSize - img.xTileOff + img.xTileSize - 1) / img.xTileSize;
						img.nYTiles = (img.ySize - img.yTileOff + img.yTileSize - 1) / img.yTileSize;
						// check for overflow before allocating memory
						if (img.nXTiles <= 0 || img.nYTiles <= 0 || img.nXTiles >= int.MaxValue / img.nYTiles)
							Error("Bad tile count in JPX SIZ marker segment");
						img.tiles = Alloc<JPXTile>((img.nXTiles * img.nYTiles));
						for (i = 0; i < img.nXTiles * img.nYTiles; ++i)
							img.tiles[i].tileComps = Alloc<JPXTileComp>(img.nComps);
						for (comp = 0; comp < img.nComps; ++comp) {
							img.tiles[0].tileComps[comp].prec = (sbyte)bufStr.ReadInt(1);
							img.tiles[0].tileComps[comp].hSep = bufStr.ReadInt(1);
							img.tiles[0].tileComps[comp].vSep = bufStr.ReadInt(1);
							if (img.tiles[0].tileComps[comp].hSep < 1 || img.tiles[0].tileComps[comp].vSep < 1)
								Error("Error in JPX SIZ marker segment");
							img.tiles[0].tileComps[comp].sgned = 0 != (img.tiles[0].tileComps[comp].prec & 0x80);
							img.tiles[0].tileComps[comp].prec = (img.tiles[0].tileComps[comp].prec & 0x7f) + 1;
							for (i = 1; i < img.nXTiles * img.nYTiles; ++i)
								img.tiles[i].tileComps[comp] = img.tiles[0].tileComps[comp].Clone();
						}
						haveSIZ = true;
						break;
					case 0x52:          // COD - coding style default
						(style, progOrder, nLayers, multiComp)
							= (GetInt(1), GetInt(1), GetInt(2), GetInt(1));
						if (!haveSIZ || multiComp == -1)
							Error("Error in JPX COD marker segment");
						tmpTile = new JPXTileComp {
							style = style, nDecompLevels = bufStr.ReadInt(1),
							codeBlockW = bufStr.ReadInt(1) + 2, codeBlockH = bufStr.ReadInt(1) + 2,
							codeBlockStyle = bufStr.ReadInt(1), transform = bufStr.ReadInt(1)
						};
						for (i = 0; i < img.nXTiles * img.nYTiles; ++i) {
							img.tiles[i].progOrder = progOrder;
							img.tiles[i].nLayers = nLayers;
							img.tiles[i].multiComp = multiComp;
							for (comp = 0; comp < img.nComps; ++comp)
								img.tiles[i].tileComps[comp].Init(tmpTile);
						}
						img.tiles[0].tileComps[0].InitResLvels(this);
						for (i = 0; i < img.nXTiles * img.nYTiles; ++i)
							for (comp = 0; comp < img.nComps; ++comp)
								if (!(i == 0 && comp == 0))
									for (r = 0; r <= tmpTile.nDecompLevels; ++r) {
										img.tiles[i].tileComps[comp].resLevels[r].precinctWidth =
											img.tiles[0].tileComps[0].resLevels[r].precinctWidth;
										img.tiles[i].tileComps[comp].resLevels[r].precinctHeight =
											img.tiles[0].tileComps[0].resLevels[r].precinctHeight;
									}
						haveCOD = true;
						break;
					case 0x53:          // COC - coding style component
						(comp, style) = (GetInt(img.nComps > 256 ? 2 : 1), GetInt(1));
						if (!haveCOD || comp >= img.nComps || style == -1)
							Error("Error in JPX COC marker segment");
						tmpTile = new JPXTileComp { nDecompLevels = bufStr.ReadInt(1),
							style = (img.tiles[0].tileComps[comp].style & ~1) | (style & 1),
							codeBlockW = bufStr.ReadInt(1) + 2, codeBlockH = bufStr.ReadInt(1) + 2,
							codeBlockStyle = bufStr.ReadInt(1), transform = bufStr.ReadInt(1)
						};
						for (i = 0; i < img.nXTiles * img.nYTiles; ++i)
							img.tiles[i].tileComps[comp].Init(tmpTile);
						img.tiles[0].tileComps[comp].InitResLvels(this);
						for (i = 1; i < img.nXTiles * img.nYTiles; ++i)
							for (r = 0; r <= img.tiles[i].tileComps[comp].nDecompLevels; ++r) {
								img.tiles[i].tileComps[comp].resLevels[r].precinctWidth
									= img.tiles[0].tileComps[comp].resLevels[r].precinctWidth;
								img.tiles[i].tileComps[comp].resLevels[r].precinctHeight
									= img.tiles[0].tileComps[comp].resLevels[r].precinctHeight;
							}
						break;
					case 0x5c:          // QCD - quantization default
						img.tiles[0].tileComps[0].quantStyle = bufStr.ReadInt(1);
						img.tiles[0].tileComps[0].InitQuantSteps(this, segLen - 3);
						for (i = 0; i < img.nXTiles * img.nYTiles; ++i)
							for (comp = 0; comp < img.nComps; ++comp)
								if (!(i == 0 && comp == 0)) {
									img.tiles[i].tileComps[comp].quantStyle = img.tiles[0].tileComps[0].quantStyle;
									img.tiles[i].tileComps[comp].quantSteps = img.tiles[0].tileComps[0].quantSteps.ToArray();
								}
						haveQCD = true;
						break;
					case 0x5d:          // QCC - quantization component
						if (!haveQCD)
							Error("JPX QCC marker segment before QCD segment");
						comp = bufStr.ReadInt(img.nComps > 256 ? 2 : 1);
						img.tiles[0].tileComps[comp].quantStyle = (sbyte)bufStr.ReadInt(1);
						img.tiles[0].tileComps[comp].InitQuantSteps(this, segLen - (img.nComps > 256 ? 5 : 4));
						for (i = 1; i < img.nXTiles * img.nYTiles; ++i) {
							img.tiles[i].tileComps[comp].quantStyle = img.tiles[0].tileComps[comp].quantStyle;
							img.tiles[i].tileComps[comp].quantSteps = img.tiles[0].tileComps[comp].quantSteps.ToArray();
						}
						break;
					case 0x90:          // SOT - start of tile
						haveSOT = true;
						break;
					case 0x5e:          // RGN - region of interest ~ ROI is unimplemented
					case 0x5f:          // POC - progression order change
					case 0x60:          // PPM - packed packet headers, main header
					case 0x55:          // TLM - tile-part lengths
					case 0x57:          // PLM - packet length, main header
					case 0x63:          // CRG - component registration
					case 0x64:          // COM - comment
					default:
						//error("Unknown marker segment {0:02x} in JPX stream:" + segType, false);
						if (segLen > 2) bufStr.Position += segLen - 2;
						break;
				}
			} while (!haveSOT);
			if (!haveSIZ) Error("Missing SIZ marker segment in JPX stream");
			if (!haveCOD) Error("Missing COD marker segment in JPX stream");
			if (!haveQCD) Error("Missing QCD marker segment in JPX stream");
			//----- read the tile-parts
			for (segType = 0x90; segType == 0x90;) { // SOT - start of tile
				ReadTilePart();
				ReadMarkerHdr(ref segType, ref segLen);
			}
			if (segType != 0xd9)        // EOC - end of codestream
				Error("Missing EOC marker in JPX codestream");
			//----- finish decoding the image
			for (i = 0; i < img.nXTiles * img.nYTiles; ++i) {
				JPXTile tile = img.tiles[i];
				if (!tile.init)
					Error("Uninitialized tile in JPX codestream");
				for (comp = 0; comp < img.nComps; ++comp)
					InverseTransform(tile.tileComps[comp]);
				InverseMultiCompAndDC(tile);
			}
		}
		void ReadTilePart() {
			int tileIdx, tilePartIdx, nTileParts, progOrder, multiComp, cbX, cbY,
				qStyle, nLayers, preCol0, preCol1, preRow0, preRow1, preCol, preRow, style,
				nx, ny, nSBs, comp, segLen = 0, i, j, r, sb, n, segType = 0, level, tilePartLen;
			JPXTileComp tmpTile;
			// process the SOT marker segment
			(tileIdx, tilePartLen, tilePartIdx, nTileParts) = (GetInt(2), GetInt(4), GetInt(1), GetInt(1));
			// check tileIdx and tilePartIdx
			// (this ignores nTileParts, because some encoders get it wrong)
			if (nTileParts == -1 || tileIdx >= img.nXTiles * img.nYTiles 
			|| tilePartIdx != img.tiles[tileIdx].nextTilePart
			|| (tilePartIdx > 0 && !img.tiles[tileIdx].init)
			|| (tilePartIdx == 0 && img.tiles[tileIdx].init))
				Error("Weird tile-part header in JPX stream");
			++img.tiles[tileIdx].nextTilePart;
			bool tilePartToEOC = tilePartLen == 0, haveSOD = false;
			tilePartLen -= 12; // subtract size of SOT segment
			do {
				ReadMarkerHdr(ref segType, ref segLen);
				tilePartLen -= 2 + segLen;
				switch (segType) {
					case 0x52:          // COD - coding style default
						(style, progOrder, nLayers, multiComp)
							= (GetInt(1), GetInt(1), GetInt(2), GetInt(1));
						if (tilePartIdx != 0 || multiComp == -1)
							Error("Error in JPX COD marker segment");
						tmpTile = new JPXTileComp { style = style, nDecompLevels = bufStr.ReadInt(1),
							codeBlockW = bufStr.ReadInt(1) + 2, codeBlockH = bufStr.ReadInt(1) + 2,
							codeBlockStyle = bufStr.ReadInt(1), transform = bufStr.ReadInt(1)
						};
						img.tiles[tileIdx].progOrder = progOrder;
						img.tiles[tileIdx].nLayers = nLayers;
						img.tiles[tileIdx].multiComp = multiComp;
						for (comp = 0; comp < img.nComps; ++comp)
							img.tiles[tileIdx].tileComps[comp].Init(tmpTile);
						img.tiles[tileIdx].tileComps[0].InitResLvels(this);
						for (comp = 1; comp < img.nComps; ++comp)
							for (r = 0; r <= tmpTile.nDecompLevels; ++r) {
								img.tiles[tileIdx].tileComps[comp].resLevels[r].precinctWidth =
									img.tiles[tileIdx].tileComps[0].resLevels[r].precinctWidth;
								img.tiles[tileIdx].tileComps[comp].resLevels[r].precinctHeight =
									img.tiles[tileIdx].tileComps[0].resLevels[r].precinctHeight;
							}
						break;
					case 0x53:          // COC - coding style component
						(comp, style) = (GetInt(img.nComps > 256 ? 2 : 1), GetInt(1));
						if (comp >= img.nComps || style == -1 || tilePartIdx != 0)
							Error("Error in JPX COC marker segment");
						tmpTile = new JPXTileComp { nDecompLevels = GetInt(1),
							style = (img.tiles[tileIdx].tileComps[comp].style & ~1) | (style & 1),
							codeBlockW = GetInt(1) + 2, codeBlockH = GetInt(1) + 2,
							codeBlockStyle = GetInt(1), transform = GetInt(1) };
						img.tiles[tileIdx].tileComps[comp].Init(tmpTile);
						img.tiles[tileIdx].tileComps[comp].InitResLvels(this);
						break;
					case 0x5c:          // QCD - quantization default
						img.tiles[tileIdx].tileComps[0].quantStyle = (sbyte)GetInt(1);
						img.tiles[tileIdx].tileComps[0].InitQuantSteps(this, segLen - 3);
						for (comp = 1; comp < img.nComps; ++comp) {
							img.tiles[tileIdx].tileComps[comp].quantStyle = img.tiles[tileIdx].tileComps[0].quantStyle;
							img.tiles[tileIdx].tileComps[comp].quantSteps = img.tiles[tileIdx].tileComps[0].quantSteps.ToArray();
						}
						break;
					case 0x5d:          // QCC - quantization component
						if (tilePartIdx != 0)
							Error("Extraneous JPX QCC marker segment");
						if ((comp = GetInt(img.nComps > 256 ? 2 : 1)) == -1 || comp >= img.nComps
						|| (img.tiles[tileIdx].tileComps[comp].quantStyle = GetInt(1)) == -1)
							Error("Error in JPX QCC marker segment");
						img.tiles[tileIdx].tileComps[comp].InitQuantSteps(this, segLen - (img.nComps > 256 ? 5 : 4));
						break;
					case 0x93:          // SOD - start of data
						haveSOD = true;
						break;
					case 0x5e:          // RGN - region of interest
					case 0x5f:          // POC - progression order change
					case 0x61:          // PPT - packed packet headers, tile-part hdr
					case 0x58:          // PLT - packet length, tile-part header
					case 0x64:          // COM - comment
					default:
						if (segLen > 2) bufStr.Position += segLen - 2;
						Error("Error in JPX marker segment " + segType);
						break;
				}
			} while (!haveSOD);
			for (comp = 0; comp < img.nComps; ++comp) {
				JPXTileComp tileComp = img.tiles[tileIdx].tileComps[comp];
				qStyle = tileComp.quantStyle & 0x1f;
				if ((qStyle == 0 && tileComp.quantSteps.Length < 3 * tileComp.nDecompLevels + 1)
				||  (qStyle == 1 && tileComp.quantSteps.Length < 1)
				||  (qStyle == 2 && tileComp.quantSteps.Length < 3 * tileComp.nDecompLevels + 1))
					Error("Too few quant steps in JPX tile part");
			}
			//----- initialize the tile, precincts, and code-blocks
			if (tilePartIdx == 0) {
				JPXTile tile = img.tiles[tileIdx];
				(i, j) = (tileIdx / img.nXTiles, tileIdx % img.nXTiles);
				tile.x0 = Math.Max(img.xOffset, img.xTileOff + j * img.xTileSize);
				tile.y0 = Math.Max(img.yOffset, img.yTileOff + i * img.yTileSize);
				tile.x1 = Math.Min(img.xSize, img.xTileOff + (j + 1) * img.xTileSize);
				tile.y1 = Math.Min(img.ySize, img.yTileOff + (i + 1) * img.yTileSize);
				tile.done = false;
				tile.maxNDecompLvls = tile.maxNPrecincts = tile.comp
						= tile.res = tile.precinct = tile.layer = 0;
				for (comp = 0; comp < img.nComps; ++comp) {
					JPXTileComp tileComp = tile.tileComps[comp];
					if (tileComp.nDecompLevels > tile.maxNDecompLvls)
						tile.maxNDecompLvls = tileComp.nDecompLevels;
					tileComp.x0 = jpxCeilDiv(tile.x0, tileComp.hSep);
					tileComp.y0 = jpxCeilDiv(tile.y0, tileComp.vSep);
					tileComp.x1 = jpxCeilDiv(tile.x1, tileComp.hSep);
					tileComp.y1 = jpxCeilDiv(tile.y1, tileComp.vSep);
					tileComp.w = tileComp.x1 - tileComp.x0;
					tileComp.h = tileComp.y1 - tileComp.y0;
					if (tileComp.w == 0 || tileComp.h == 0 || tileComp.w > int.MaxValue / tileComp.h)
						Error("Invalid tile size or sample separation in JPX stream");
					tileComp.data = new int[tileComp.w * tileComp.h];
					n = Math.Max(tileComp.x1 - tileComp.x0, tileComp.y1 - tileComp.y0);
					tileComp.buf = new int[n + 8];
					for (r = 0; r <= tileComp.nDecompLevels; ++r) {
						JPXResLevel resLvl = tileComp.resLevels[r];
						resLvl.x0 = jpxCeilDivPow2(tileComp.x0, tileComp.nDecompLevels - r);
						resLvl.y0 = jpxCeilDivPow2(tileComp.y0, tileComp.nDecompLevels - r);
						resLvl.x1 = jpxCeilDivPow2(tileComp.x1, tileComp.nDecompLevels - r);
						resLvl.y1 = jpxCeilDivPow2(tileComp.y1, tileComp.nDecompLevels - r);
						resLvl.codeBlockW = Math.Min(resLvl.precinctWidth - (r == 0 ? 0 : 1), tileComp.codeBlockW);
						resLvl.codeBlockH = Math.Min(resLvl.precinctHeight - (r == 0 ? 0 : 1), tileComp.codeBlockH);
						resLvl.cbW = 1 << resLvl.codeBlockW;
						resLvl.cbH = 1 << resLvl.codeBlockH;
						// the JPEG 2000 spec says that packets for empty res levels
						// should all be present in the codestream (B.6, B.9, B.10),
						// but it appears that encoders drop packets if the res level
						// AND the subbands are all completely empty
						resLvl.empty = resLvl.x0 == resLvl.x1 || resLvl.y0 == resLvl.y1;
						if (r == 0) {
							nSBs = 1;
							(resLvl.bx0[0], resLvl.by0[0]) = (resLvl.x0, resLvl.y0);
							(resLvl.bx1[0], resLvl.by1[0]) = (resLvl.x1, resLvl.y1);
							resLvl.empty = resLvl.empty
								&& (resLvl.bx0[0] == resLvl.bx1[0] || resLvl.by0[0] == resLvl.by1[0]);
						}
						else {
							nSBs = 3;
							resLvl.bx0[0] = jpxCeilDivPow2(resLvl.x0 - 1, 1);
							resLvl.by0[0] = jpxCeilDivPow2(resLvl.y0, 1);
							resLvl.bx1[0] = jpxCeilDivPow2(resLvl.x1 - 1, 1);
							resLvl.by1[0] = jpxCeilDivPow2(resLvl.y1, 1);
							resLvl.bx0[1] = jpxCeilDivPow2(resLvl.x0, 1);
							resLvl.by0[1] = jpxCeilDivPow2(resLvl.y0 - 1, 1);
							resLvl.bx1[1] = jpxCeilDivPow2(resLvl.x1, 1);
							resLvl.by1[1] = jpxCeilDivPow2(resLvl.y1 - 1, 1);
							resLvl.bx0[2] = jpxCeilDivPow2(resLvl.x0 - 1, 1);
							resLvl.by0[2] = jpxCeilDivPow2(resLvl.y0 - 1, 1);
							resLvl.bx1[2] = jpxCeilDivPow2(resLvl.x1 - 1, 1);
							resLvl.by1[2] = jpxCeilDivPow2(resLvl.y1 - 1, 1);
							resLvl.empty = resLvl.empty
								&& (resLvl.bx0[0] == resLvl.bx1[0] || resLvl.by0[0] == resLvl.by1[0])
								&& (resLvl.bx0[1] == resLvl.bx1[1] || resLvl.by0[1] == resLvl.by1[1])
								&& (resLvl.bx0[2] == resLvl.bx1[2] || resLvl.by0[2] == resLvl.by1[2]);
						}
						preCol0 = jpxFloorDivPow2(resLvl.x0, resLvl.precinctWidth);
						preCol1 = jpxCeilDivPow2(resLvl.x1, resLvl.precinctWidth);
						preRow0 = jpxFloorDivPow2(resLvl.y0, resLvl.precinctHeight);
						preRow1 = jpxCeilDivPow2(resLvl.y1, resLvl.precinctHeight);
						int nPrecincts = (preCol1 - preCol0) * (preRow1 - preRow0);
						resLvl.precincts = Alloc<JPXPrecinct>(nPrecincts);
						tile.maxNPrecincts = Math.Max(tile.maxNPrecincts, nPrecincts);
						int pIdx = 0;
						for (preRow = preRow0; preRow < preRow1; ++preRow) {
							for (preCol = preCol0; preCol < preCol1; ++preCol, ++pIdx) {
								JPXPrecinct precinct = resLvl.precincts[pIdx];
								precinct.subbands = Alloc<JPXSubband>(nSBs);
								for (sb = 0; sb < nSBs; ++sb) {
									precinct.subbands[sb].inclusion = null;
									precinct.subbands[sb].BitPlane0 = null;
									precinct.subbands[sb].cbs = null;
								}
								for (sb = 0; sb < nSBs; ++sb) {
									JPXSubband subband = precinct.subbands[sb];
									int off = (r == 0) ? 0 : 1;
									int px0 = Math.Max(resLvl.bx0[sb], preCol << (resLvl.precinctWidth - off));
									int px1 = Math.Min(resLvl.bx1[sb], (preCol + 1) << (resLvl.precinctWidth - off));
									int py0 = Math.Max(resLvl.by0[sb], preRow << (resLvl.precinctHeight - off));
									int py1 = Math.Min(resLvl.by1[sb], (preRow + 1) << (resLvl.precinctHeight - off));
									int sbCoeffs;
									if (r == 0)  // (NL)LL
										sbCoeffs = 0;
									else if (sb == 0)  // (NL-r+1)HL
										sbCoeffs = resLvl.bx1[1] - resLvl.bx0[1];
									else if (sb == 1)  // (NL-r+1)LH
										sbCoeffs = ((resLvl.by1[0] - resLvl.by0[0]) * tileComp.w);
									else  // (NL-r+1)HH
										sbCoeffs = ((resLvl.by1[0] - resLvl.by0[0]) * tileComp.w
												  + (resLvl.bx1[1] - resLvl.bx0[1]));
									int cbCol0 = jpxFloorDivPow2(px0, resLvl.codeBlockW);
									int cbCol1 = jpxCeilDivPow2(px1,  resLvl.codeBlockW);
									int cbRow0 = jpxFloorDivPow2(py0, resLvl.codeBlockH);
									int cbRow1 = jpxCeilDivPow2(py1,  resLvl.codeBlockH);
									subband.nXCBs = cbCol1 - cbCol0;
									subband.nYCBs = cbRow1 - cbRow0;
									n = Math.Max(subband.nXCBs, subband.nYCBs);
									for (subband.maxTTLevel = 0, --n; 0 != n; ++subband.maxTTLevel, n >>= 1)
										;
									for (n = 0, level = subband.maxTTLevel; level >= 0; --level) {
										nx = jpxCeilDivPow2(subband.nXCBs, level);
										ny = jpxCeilDivPow2(subband.nYCBs, level);
										n += nx * ny;
									}
									subband.inclusion = Alloc<JPXTagTreeNode>(n);
									subband.BitPlane0 = Alloc<JPXTagTreeNode>(n);
									subband.cbs = Alloc<JPXCodeBlock>((subband.nXCBs * subband.nYCBs));
									int cbi = 0;
									for (cbY = cbRow0; cbY < cbRow1; ++cbY) {
										for (cbX = cbCol0; cbX < cbCol1; ++cbX, ++cbi) {
											var cb = subband.cbs[cbi];
											cb.x0 = Math.Max(px0, cbX << resLvl.codeBlockW);
											cb.y0 = Math.Max(py0, cbY << resLvl.codeBlockH);
											cb.x1 = Math.Min(px1, cb.x0 + resLvl.cbW);
											cb.y1 = Math.Min(py1, cb.y0 + resLvl.cbH);
											(cb.seen, cb.lBlock, cb.nextPass, cb.nBitPlanes0, cb.dataLen)
												= (false, 3, jpxPassCleanup, 0, new int[1]);
											if (r <= tileComp.nDecompLevels) {
												cb.coeffs = tileComp.data;
												cb.coefOff = sbCoeffs + (cb.x0 - resLvl.bx0[sb])
														 + tileComp.w * (cb.y0 - resLvl.by0[sb]);
												cb.touched = new bool[1 << (resLvl.codeBlockW + resLvl.codeBlockH)];
											}
											else
												(cb.coeffs, cb.touched) = (null, null);
											cb.len = 0;
										}
									}
								}
							}
						}
					}
				}
				tile.init = true;
			}
			ReadTilePartData(tileIdx, tilePartLen, tilePartToEOC);
		}
		void ReadTilePartData(int tileIdx, int tilePartLen, bool tilePartToEOC) {
			int bits = 0, nx, ny, cbX, cbY, sb, j, i, ttVal, level, n;
			JPXTile tile = img.tiles[tileIdx];
			for (; true;) {         // read all packets from this tile-part
				if (tile.done)          // if the tile is finished, skip any remaining data
					bufStr.Position += tilePartLen;
				if (!tilePartToEOC && tilePartLen == 0)
					break;
				JPXTileComp tileComp = tile.tileComps[tile.comp];
				JPXResLevel resLevel = tileComp.resLevels[tile.res];
				JPXPrecinct precinct = resLevel.precincts[tile.precinct];
				if (resLevel.empty)
					goto nextPacket;
				//----- packet header
				StartBitBuf(tilePartLen);               // setup
				if (0 != (tileComp.style & 0x02))
					SkipSOP();
				if (!ReadBits(1, ref bits))             // zero-length flag
					Error("Error in JPX stream");
				if (0 == bits)      // packet is empty -- clear all code-block inclusion flags
					for (sb = 0; sb < (tile.res == 0 ? 1 : 3); ++sb) {
						JPXSubband subband = precinct.subbands[sb];
						for (cbY = 0; cbY < subband.nYCBs; ++cbY)
							for (cbX = 0; cbX < subband.nXCBs; ++cbX)
								subband.cbs[cbY * subband.nXCBs + cbX].included = 0;
					}
				else
					for (sb = 0; sb < (tile.res == 0 ? 1 : 3); ++sb) {
						JPXSubband subband = precinct.subbands[sb];
						for (cbY = 0; cbY < subband.nYCBs; ++cbY)
							for (cbX = 0; cbX < subband.nXCBs; ++cbX) {
								JPXCodeBlock cb = subband.cbs[cbY * subband.nXCBs + cbX];
								if (cb.x0 >= cb.x1 || cb.y0 >= cb.y1) {
									cb.included = 0;        // skip code-blocks with no coefficients
									continue;
								}
								if (cb.seen) {              // code-block inclusion
									if (!ReadBits(1, ref cb.included))
										Error("Error in JPX stream");
								}
								else {
									for (ttVal = i = 0, level = subband.maxTTLevel; level >= 0; --level) {
										nx = jpxCeilDivPow2(subband.nXCBs, level);
										ny = jpxCeilDivPow2(subband.nYCBs, level);
										j = i + (cbY >> level) * nx + (cbX >> level);
										if (!subband.inclusion[j].finished && 0 == subband.inclusion[j].val)
											subband.inclusion[j].val = ttVal;
										else
											ttVal = subband.inclusion[j].val;
										while (!subband.inclusion[j].finished && ttVal <= tile.layer) {
											if (!ReadBits(1, ref bits))
												Error("Error in JPX stream");
											if (bits == 1)
												subband.inclusion[j].finished = true;
											else
												++ttVal;
										}
										subband.inclusion[j].val = ttVal;
										if (ttVal > tile.layer)
											break;
										i += nx * ny;
									}
									cb.included = level < 0 ? 1 : 0;
								}
								if (cb.included != 0) {
									if (!cb.seen) {     // zero bit-plane count
										for (ttVal = i = 0, level = subband.maxTTLevel; level >= 0;
												--level, i += nx * ny) {
											nx = jpxCeilDivPow2(subband.nXCBs, level);
											ny = jpxCeilDivPow2(subband.nYCBs, level);
											j = i + (cbY >> level) * nx + (cbX >> level);
											if (!subband.BitPlane0[j].finished && 0 == subband.BitPlane0[j].val)
												subband.BitPlane0[j].val = ttVal;
											else
												ttVal = subband.BitPlane0[j].val;
											while (!subband.BitPlane0[j].finished) {
												if (!ReadBits(1, ref bits))
													Error("Error in JPX stream");
												if (bits == 1)
													subband.BitPlane0[j].finished = true;
												else
													++ttVal;
											}
											subband.BitPlane0[j].val = ttVal;
										}
										cb.nBitPlanes0 = ttVal;
									}
									if (!ReadBits(1, ref bits)) // number of coding passes
										Error("Error in JPX stream");
									if (bits == 0)
										cb.nCodingPasses = 1;
									else if (ReadBits(1, ref bits) && bits == 0)
										cb.nCodingPasses = 2;
									else if (ReadBits(2, ref bits) && bits < 3)
										cb.nCodingPasses = 3 + bits;
									else if (ReadBits(5, ref bits) && bits < 31)
										cb.nCodingPasses = 6 + bits;
									else if (ReadBits(7, ref bits))
										cb.nCodingPasses = 37 + bits;
									else
										Error("Error in JPX stream");
									for (; true; ++cb.lBlock) {     // update Lblock
										if (!ReadBits(1, ref bits))
											Error("Error in JPX stream");
										if (0 == bits)
											break;
									}
									// one codeword segment for each of the coding passes
									if (0 != (tileComp.codeBlockStyle & 0x04)) {
										if (cb.nCodingPasses > cb.dataLen.Length)
											ReAlloc(ref cb.dataLen, cb.nCodingPasses);
										for (i = 0; i < cb.nCodingPasses; ++i)
											if (!ReadBits(cb.lBlock, ref cb.dataLen[i]))
												Error("Error in JPX stream");
									}
									else {  // read the length
										for (n = cb.lBlock, i = cb.nCodingPasses >> 1; i != 0; ++n, i >>= 1)
											;
										if (!ReadBits(n, ref cb.dataLen[0]))
											Error("Error in JPX stream");
									}
								}
							}
					}
				if (0 != (tileComp.style & 0x04))
					SkipEPH();
				tilePartLen = FinishBitBuf();			//----- packet data
				for (sb = 0; sb < (tile.res == 0 ? 1 : 3); ++sb) {
					JPXSubband subband = precinct.subbands[sb];
					for (cbY = 0; cbY < subband.nYCBs; ++cbY)
						for (cbX = 0; cbX < subband.nXCBs; ++cbX) {
							JPXCodeBlock cb = subband.cbs[cbY * subband.nXCBs + cbX];
							if (0 != cb.included) {
								ReadCodeBlockData(tileComp, resLevel, tile.res, sb, cb);
								if (0 != (tileComp.codeBlockStyle & 0x04))
									for (i = 0; i < cb.nCodingPasses; ++i)
										tilePartLen -= cb.dataLen[i];
								else
									tilePartLen -= cb.dataLen[0];
								cb.seen = true;
							}
						}
				}
			nextPacket:									//----- next packet
				do {
					if (tile.progOrder == 0) // layer, resolution level, component, precinct
						tile.done = nextTile(ref tile.precinct, tile.maxNPrecincts, ref tile.comp, img.nComps,
								ref tile.res, tile.maxNDecompLvls + 1, ref tile.layer, tile.nLayers);
					else if (tile.progOrder == 1) // resolution level, layer, component, precinct
						tile.done = nextTile(ref tile.precinct, tile.maxNPrecincts, ref tile.comp, img.nComps,
								ref tile.layer, tile.nLayers, ref tile.res, tile.maxNDecompLvls + 1);
					else if (tile.progOrder == 2) // resolution level, precinct, component, layer - incorrect if there are subsampled components (?)
						tile.done = nextTile(ref tile.layer, tile.nLayers, ref tile.comp, img.nComps,
								ref tile.precinct, tile.maxNPrecincts, ref tile.res, tile.maxNPrecincts + 1);
					else if (tile.progOrder == 3) // precinct, component, resolution level, layer - incorrect if there are subsampled components (?)
						tile.done = nextTile(ref tile.layer, tile.nLayers, ref tile.res, tile.maxNDecompLvls + 1,
								ref tile.comp, img.nComps, ref tile.precinct, tile.maxNPrecincts);
					else if (tile.progOrder == 4) // component, precinct, resolution level, layer
						tile.done = nextTile(ref tile.layer, tile.nLayers, ref tile.res, tile.maxNDecompLvls + 1,
								ref tile.precinct, tile.maxNPrecincts, ref tile.comp, img.nComps);
				} while (!tile.done && (tile.res > tile.tileComps[tile.comp].nDecompLevels
								|| tile.precinct >= tile.tileComps[tile.comp].resLevels[tile.res].precincts.Length));
			}
			return;
			bool nextTile(ref int v1, int m1, ref int v2, int m2, ref int v3, int m3, ref int v4, int m4) {
				if (++v1 != m1) return false;
				v1 = 0;
				if (++v2 != m2) return false;
				v2 = 0;
				if (++v3 != m3) return false;
				v3 = 0;
				if (++v4 != m4) return false;
				v4 = 0;
				return true;
			}
		}
		void ReadCodeBlockData(JPXTileComp tileComp, JPXResLevel resLevel, int res, int sb, JPXCodeBlock cb) {
			int n, i;
			if (res > tileComp.nDecompLevels) { // skip the codeblock data
				if (0 != (tileComp.codeBlockStyle & 0x04))
					for (n = 0, i = 0; i < cb.nCodingPasses; ++i)
						n += cb.dataLen[i];
				else
					n = cb.dataLen[0];
				bufStr.Position += n;
				return;
			}
			if (null != cb.arithDecoder) {
				if (0 != (tileComp.codeBlockStyle & 0x04)) {
					cb.arithDecoder.SetStream(bufStr, cb.dataLen[0]);
					cb.arithDecoder.Start();
				}
				else
					cb.arithDecoder.Restart(cb.dataLen[0]);
			}
			else {
				cb.arithDecoder = new ArithmDecoder();
				cb.arithDecoder.SetStream(bufStr, cb.dataLen[0]);
				cb.arithDecoder.Start();
				cb.stats = new CXStats(jpxNContexts);
				cb.stats.SetEntry(jpxContextSigProp, 4, 0);
				cb.stats.SetEntry(jpxContextRunLen, 3, 0);
				cb.stats.SetEntry(jpxContextUniform, 46, 0);
			}
			for (i = 0; i < cb.nCodingPasses; ++i) {
				if (0 != (tileComp.codeBlockStyle & 0x04) && i > 0) {
					cb.arithDecoder.SetStream(bufStr, cb.dataLen[i]);
					cb.arithDecoder.Start();
				}
				switch (cb.nextPass) {
					case jpxPassSigProp:	SignificancePropagationPass(tileComp, resLevel, res == 0 ? 1 : sb, cb); break;
					case jpxPassMagRef:		MagnitudeRefinementPass(tileComp, resLevel, cb); break;
					case jpxPassCleanup:	CleanupPass(tileComp, resLevel, res == 0 ? 1 : sb, cb);	break;
				}
				if (0 != (tileComp.codeBlockStyle & 0x02)) {
					cb.stats.Reset();
					cb.stats.SetEntry(jpxContextSigProp, 4, 0);
					cb.stats.SetEntry(jpxContextRunLen, 3, 0);
					cb.stats.SetEntry(jpxContextUniform, 46, 0);
				}
				if (0 != (tileComp.codeBlockStyle & 0x04))
					cb.arithDecoder.Cleanup();
			}
			cb.arithDecoder.Cleanup();
		}

		private static void CleanupPass(JPXTileComp tileComp, JPXResLevel resLvl, int res, JPXCodeBlock cb) {
			int[] coeffd = cb.coeffs, sigProp = sigPropCtx[res];
			bool[] touchd = cb.touched;
			int c0i, c1i, c2i, t0i, t1i, t2i, x, y0, y1, cc, tw=tileComp.w, cbw=resLvl.cbW;
			bool cbs8 = 0 == (tileComp.codeBlockStyle & 0x08);
			for (y0 = cb.y0, c1i = cb.coefOff, t0i=0; y0 < cb.y1;
					y0 += 4, c1i+=4*tw, t0i+=4<<resLvl.codeBlockW) {
				for (x = cb.x0, c2i=c1i, t1i=t0i; x < cb.x1; ++x, ++c2i, ++t1i) {
					y1 = 0;
					bool cx0 = x==cb.x0, cy0 = y0==cb.y0, cx1 = x==cb.x1-1, cy1 = y0+4==cb.y1;
					if (y0 + 3 < cb.y1
					&& !touchd[t1i]
					&& !touchd[t1i+cbw]
					&& !touchd[t1i+cbw*2]
					&& !touchd[t1i+cbw*3]
					&& (	   cy0 || 0 == coeffd[c2i-tw])
					&& (cx0 || cy0 || 0 == coeffd[c2i-tw-1])
					&& (cx1 || cy0 || 0 == coeffd[c2i-tw+1])
					&& (cx0 || (0 == coeffd[c2i-1]
							 && 0 == coeffd[c2i-1+tw]
							 && 0 == coeffd[c2i-1+tw*2]
							 && 0 == coeffd[c2i-1+tw*3]))
					&& (cx1 || (0 == coeffd[c2i+1]
							 && 0 == coeffd[c2i+1+tw]
							 && 0 == coeffd[c2i+1+tw*2]
							 && 0 == coeffd[c2i+1+tw*3]))
					&& (!cbs8 || ((    cy1 || 0 == coeffd[c2i+4*tw])
							&& (cx0 || cy1 || 0 == coeffd[c2i+4*tw-1])
							&& (cx1 || cy1 || 0 == coeffd[c2i+4*tw+1])))) {
						if (0 != cb.arithDecoder.DecodeBit(jpxContextRunLen, cb.stats)) {
							y1 = cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats);
							y1 = (y1 << 1) | cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats);
							c0i = c2i + y1 * tw;                // signContext[12-0sign]=9
							coeffd[c0i] = (0 != cb.arithDecoder.DecodeBit(9, cb.stats)) ? -1 : 1;
							++y1;
						}
						else
							continue;
					}
					for (c0i=c2i+y1*tw, t2i=t1i+(y1<<resLvl.codeBlockW);
							y1 < 4 && y0 + y1 < cb.y1; ++y1, c0i+=tw, t2i+=cbw) {
						if (touchd[t2i]) {
							touchd[t2i] = false;
							continue;
						}
						int pi = 0, si = 12;
						bool chkNotFst = y0 + y1 > cb.y0,
							chkNotLst = y0 + y1 < cb.y1 - 1 && (cbs8 || y1 < 3);
						if (x > cb.x0) {
							if (0 != (cc = coeffd[c0i-1])) {
								pi += sigPropCtxH;
								si += cc < 0 ? -sigPropCtxV : sigPropCtxV;
							}
							if (chkNotFst && 0 != coeffd[c0i-tw-1]) pi++;
							if (chkNotLst && 0 != coeffd[c0i+tw-1]) pi++;
						}
						if (x < cb.x1 - 1) {
							if (0 != (cc = coeffd[c0i+1])) {
								pi += sigPropCtxH;
								si += cc < 0 ? -sigPropCtxV : sigPropCtxV;
							}
							if (chkNotFst && 0 != coeffd[c0i-tw+1]) pi++;
							if (chkNotLst && 0 != coeffd[c0i+tw+1]) pi++;
						}
						if (chkNotFst && 0 != (cc = coeffd[c0i-tw])) {
							pi += sigPropCtxV;
							si += cc < 0 ? -1 : 1;
						}
						if (chkNotLst && 0 != (cc=coeffd[c0i+tw])) {
							pi += sigPropCtxV;
							si += cc < 0 ? -1 : 1;
						}
						int cx = sigProp[pi];
						if (0 != cb.arithDecoder.DecodeBit(cx, cb.stats)) {
							var sc = signContext[si];
							coeffd[c0i] = 0==(cb.arithDecoder.DecodeBit(sc&0x0f,cb.stats)
												^ (sc >> 4)) ? 1 : -1;
						}
					}
				}
			}
			++cb.len;
			if (0 != (tileComp.codeBlockStyle & 0x20)) {    // look for a segmentation symbol
				int segSym = cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats) << 3;
				segSym |= cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats) << 2;
				segSym |= cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats) << 1;
				segSym |= cb.arithDecoder.DecodeBit(jpxContextUniform, cb.stats);
				if (segSym != 0x0a) // in theory this should be a fatal error, but it seems to be problematic
					Error("Missing or invalid segmentation symbol in JPX stream", false);
			}
			cb.nextPass = jpxPassSigProp;
		}

		void MagnitudeRefinementPass(JPXTileComp tileComp, JPXResLevel resLevel, JPXCodeBlock cb) {
			int[] coeff = cb.coeffs;
			bool[] touch = cb.touched;
			int x, y0, y1, cc, c0i, c1i, c2i, t0i, t1i, t2i, tw = tileComp.w;
			bool cbs8 = 0 == (tileComp.codeBlockStyle & 0x08);
			for (y0=cb.y0, c0i=cb.coefOff, t0i=0; y0<cb.y1; y0+=4, c0i+=4*tw, t0i+=(4<<resLevel.codeBlockW)) 
				for (x=cb.x0, c1i=c0i, t1i=t0i; x<cb.x1; ++x, c1i++, t1i++) {
					for (y1 = 0, c2i=c1i, t2i=t1i; y1<4 && y0+y1<cb.y1; 
							++y1, c2i+=tw, t2i+=resLevel.cbW) {
						if ((cc = coeff[c2i]) != 0 && !touch[t2i]) {
							int cx = 16;
							if (cc == 1 || cc == -1) {
								int all = 0;
								bool cy0 = y0 + y1 > cb.y0, 
									 cy1 = y0 + y1 < cb.y1 - 1 && (cbs8 || y1 < 3);
								if (x > cb.x0) {
									all += 0 != coeff[c2i-1] ? 1 : 0;
									if (cy0 && 0 != coeff[c2i-tw-1]) all++;
									if (cy1 && 0 != coeff[c2i+tw-1]) all++;
								}
								if (x < cb.x1 - 1) {
									all += 0 != coeff[c2i+1] ? 1 : 0;
									if (cy0 && 0 != coeff[c2i-tw+1]) all++;
									if (cy1 && 0 != coeff[c2i+tw+1]) all++;
								}
								if (cy0 && 0 != coeff[c2i-tw]) all++;
								if (cy1 && 0 != coeff[c2i+tw]) all++;
								cx = all != 0 ? 15 : 14;
							}
							int bit = cb.arithDecoder.DecodeBit(cx, cb.stats);
							coeff[c2i] = (cc << 1) + (cc < 0 ? -bit : +bit);
							touch[t2i] = true;
						}
					}
				}
			++cb.nextPass;
		}

		int SignificancePropagationPass(JPXTileComp tileComp, JPXResLevel resLevel, int res, JPXCodeBlock cb) {
			int x, y0, y1, cc, c0i, c1i, c2i, t0i, t1i, t2i, tw = tileComp.w;
			int[] coeff = cb.coeffs, sigProp = sigPropCtx[res];
			bool[] touch = cb.touched;
			bool cbs8 = 0 == (tileComp.codeBlockStyle & 0x08);
			for (y0=cb.y0, c1i=cb.coefOff, t1i = 0; y0 < cb.y1;
					y0 += 4, c1i+=4*tw, t1i += 4 << resLevel.codeBlockW) {
				for (x=cb.x0, c2i=c1i, t2i=t1i; x < cb.x1; ++x, c2i++, t2i++) {
					for (y1=0, c0i=c2i, t0i=t2i; y1 < 4 && y0 + y1 < cb.y1;
							++y1, c0i += tw, t0i += resLevel.cbW) {
						if (0 == coeff[c0i]) {
							int pi = 0, si = 12;
							bool chkNotFst = y0 + y1 > cb.y0, 
								chkNotLst = y0 + y1 < cb.y1 - 1 && (cbs8 || y1 < 3);
							if (x > cb.x0) {
								if (0 != (cc = coeff[c0i-1])) {
									pi += sigPropCtxH;
									si += cc < 0 ? -sigPropCtxV : sigPropCtxV;
								}
								if (chkNotFst && 0 != coeff[c0i-tw-1]) pi++;
								if (chkNotLst && 0 != coeff[c0i+tw-1]) pi++;
							}
							if (x < cb.x1 - 1) {
								if (0 != (cc = coeff[c0i+1])) {
									pi += sigPropCtxH;
									si += cc < 0 ? -sigPropCtxV : sigPropCtxV;
								}
								if (chkNotFst && 0 != coeff[c0i-tw+1]) pi++;
								if (chkNotLst && 0 != coeff[c0i+tw+1]) pi++;
							}
							if (chkNotFst && 0 != (cc = coeff[c0i-tw])) {
								pi += sigPropCtxV;
								si += cc < 0 ? -1 : 1;
							}
							if (chkNotLst && 0 != (cc = coeff[c0i+tw])) {
								pi += sigPropCtxV;
								si += cc < 0 ? -1 : 1;
							}
							int cx = sigProp[pi];
							if (cx != 0) {
								if (0 != cb.arithDecoder.DecodeBit(cx, cb.stats)) {
									var sc = signContext[si];
									coeff[c0i] = 0==(cb.arithDecoder.DecodeBit(sc&0x0f, cb.stats)
														^ (sc >> 4)) ? 1 : -1;
								}
								touch[t0i] = true;
							}
						}
					}
				}
			}
			++cb.nextPass;
			return y0;
		}

		void InverseTransform(JPXTileComp tileComp) {
			int eps, shift, r, x, y, shift2, c0i, c1i, t0i, t1i;
			double mu;
			JPXResLevel resLevel = tileComp.resLevels[0];   //----- (NL)LL subband (resolution level 0)
			int qStyle = tileComp.quantStyle & 0x1f;      // i-quant parameters
			int guard = (tileComp.quantStyle >> 5) & 7;
			if (qStyle == 0) {
				eps = (tileComp.quantSteps[0] >> 3) & 0x1f;
				shift = guard + eps - 1;
				mu = 0;
			}
			else {
				shift = guard - 1 + tileComp.prec;
				mu = (0x800 + (tileComp.quantSteps[0] & 0x7ff)) / 2048.0;
			}
			if (tileComp.transform == 0)
				shift += fracBits - tileComp.prec;
			// do fixed point adjustment and dequantization on (NL)LL
			for (int pre = 0; pre < resLevel.precincts.Length; ++pre) {
				JPXSubband subband = resLevel.precincts[pre].subbands[0];
				for (int cbY = 0, cbi = 0; cbY < subband.nYCBs; ++cbY)
					for (int cbX = 0; cbX < subband.nXCBs; ++cbX, cbi++) {
						JPXCodeBlock cb = subband.cbs[cbi];
						int[] coeffd = cb.coeffs;
						bool[] touch = cb.touched;
						for (y = cb.y0, c0i = cb.coefOff, t0i=0;
									 y < cb.y1; ++y, c0i+=tileComp.w, t0i+=resLevel.cbW) {
							for (x=cb.x0, c1i=c0i, t1i=t0i; x < cb.x1; ++x, ++c1i, ++t1i) {
								int val = coeffd[c1i];
								if (val != 0) {
									shift2 = shift 
										- (cb.nBitPlanes0 + cb.len + (touch[t1i] ? 1 : 0));
									if (shift2 > 0)
										val = (val << shift2) 
											+ (val<0 ? -(1<<(shift2-1)) : (1<<(shift2-1)));
									else
										val >>= -shift2;
									if (qStyle != 0)
										val = (int)(mu * val);
									else if (tileComp.transform == 0)
										val &= -1 << (fracBits - tileComp.prec);
								}
								coeffd[c1i] = val;
							}
						}
					}
			}
			//----- IDWT for each level
			// (n)LL is already in the upper-left corner of the
			// tile-component data array -- interleave with (n)HL/LH/HH
			// and inverse transform to get (n-1)LL, which will be stored
			// in the upper-left corner of the tile-component data array
			for (r = 1; r <= tileComp.nDecompLevels; ++r)
				InverseTransformLevel(tileComp, r, tileComp.resLevels[r]);
		}
		void InverseTransformLevel(JPXTileComp tileComp, int r, JPXResLevel resLevel) {
			double mu;
			int shift, t, x, y, c0i, c1i, t0i, t1i;
			int qStyle = tileComp.quantStyle & 0x1f;
			int guard = (tileComp.quantStyle >> 5) & 7, eps;
			int nx1 = resLevel.bx1[1] - resLevel.bx0[1];
			int nx2 = nx1 + resLevel.bx1[0] - resLevel.bx0[0];
			int ny1 = resLevel.by1[0] - resLevel.by0[0];
			int ny2 = ny1 + resLevel.by1[1] - resLevel.by0[1];
			if (nx2 == 0 || ny2 == 0)
				return;
			for (int sb = 0; sb < 3; ++sb) {    //----- fixed-point adjustment and dequantization
				if (qStyle == 0) {          // i-quant parameters
					eps = (tileComp.quantSteps[3 * r - 2 + sb] >> 3) & 0x1f;
					shift = guard + eps - 1;
					mu = 0; // make gcc happy
				}
				else {
					shift = guard + tileComp.prec;
					if (sb == 2) ++shift;
					t = tileComp.quantSteps[qStyle == 1 ? 0 : (3 * r - 2 + sb)];
					mu = (0x800 + (t & 0x7ff)) / 2048.0;
				}
				if (tileComp.transform == 0)
					shift += fracBits - tileComp.prec;
				// fixed point adjustment and dequantization
				for (int pre = 0; pre < resLevel.precincts.Length; ++pre) {
					JPXPrecinct precinct = resLevel.precincts[pre];
					JPXSubband subband = precinct.subbands[sb];
					for (int cbi = 0, cbY = 0; cbY < subband.nYCBs; ++cbY) {
						for (int cbX = 0; cbX < subband.nXCBs; ++cbX, cbi++) {
							JPXCodeBlock cb = subband.cbs[cbi];
							int[] coeff = cb.coeffs;
							bool[] touch = cb.touched;
							for (y=cb.y0, c0i=cb.coefOff, t0i=0;
								 y < cb.y1; ++y, c0i+=tileComp.w, t0i+=resLevel.cbW) {
								for (x=cb.x0, c1i=c0i, t1i=t0i; x < cb.x1; ++x, ++c1i, ++t1i) {
									int val = coeff[c1i];
									if (val != 0) {
										int sh2 = shift-(cb.nBitPlanes0+cb.len+(touch[t1i]?1:0));
										if (sh2 > 0)
											val = (val << sh2)
												+ (val<0 ? -(1<<(sh2-1)) : (1<<(sh2-1)));
										else
											val >>= -sh2;
										if (qStyle != 0)
											val = (int)(val * mu);
										else if (tileComp.transform == 0)
											val &= -1 << (fracBits - tileComp.prec);
										coeff[c1i] = val;
									}
								}
							}
						}
					}
				}
			}
			//----- inverse transform
			int[] pData = tileComp.data, pBuf = tileComp.buf;
			int offset = 3 + (resLevel.x0 & 1);                                     // horizontal (row) transforms
			int o1, o2, iData, iBuf;
			(o1, o2) = resLevel.bx0[0] == resLevel.bx0[1] ? (0, 1) : (1, 0);
			for (y = iData = 0; y < ny2; ++y, iData += tileComp.w) {
				for (x = 0, iBuf = offset + o1; x < nx1; ++x, iBuf += 2)
					pBuf[iBuf] = pData[iData + x];        // fetch LL/LH
				for (x = nx1, iBuf = offset + o2; x < nx2; ++x, iBuf += 2)
					pBuf[iBuf] = pData[iData + x];        // fetch LL/LH
				InverseTransform1D(tileComp, tileComp.buf, offset, nx2);
				Array.Copy(pBuf, offset, pData, iData, nx2);
			}
			offset = 3 + (resLevel.y0 & 1);                                         // vertical (column) transforms
			(o1, o2) = resLevel.by0[0] == resLevel.by0[1] ? (0, 1) : (1, 0);
			for (x = 0, iData = 0; x < nx2; ++x, ++iData) {
				for (y = 0, iBuf = offset + o1; y < ny1; ++y, iBuf += 2)
					pBuf[iBuf] = pData[iData + y * tileComp.w];   // fetch LL/HL
				for (y = ny1, iBuf = offset + o2; y < ny2; ++y, iBuf += 2)
					pBuf[iBuf] = pData[iData + y * tileComp.w];   // fetch LH/HH
				InverseTransform1D(tileComp, tileComp.buf, offset, ny2);
				for (y = 0, iBuf = offset; y < ny2; ++y, ++iBuf)
					pData[iData + y * tileComp.w] = pBuf[iBuf];
			}
		}
		// constants used in the IDWT
		const double idwtAlpha = -1.586134342059924, idwtBeta = -0.052980118572961,
			idwtGamma = 0.882911075530934, idwtDelta = 0.443506852043971,
			idwtKappa = 1.230174104914001, idwtIKappa = (1.0 / idwtKappa);
		void InverseTransform1D(JPXTileComp tileComp, int[] data, int offset, int n) {
			if (n != 1) {
				int end = offset + n, i;
				data[end] = data[end - 2];
				if (n == 2) {
					data[end + 1] = data[end + 3] = data[offset + 1];
					data[end + 2] = data[offset];
				}
				else {
					data[end + 1] = data[end - 3];
					if (n == 3) {
						data[end + 2] = data[offset + 1];
						data[end + 3] = data[offset + 2];
					}
					else {
						data[end + 2] = data[end - 4];
						data[end + 3] = data[(n == 4) ? offset + 1 : end - 5];
					}
				}
				data[offset - 1] = data[offset + 1];					// extend left
				data[offset - 2] = data[offset + 2];
				data[offset - 3] = data[offset + 3];
				if (offset == 4)
					data[0] = data[offset + 4];
				if (tileComp.transform == 0) {							// 9-7 irreversible filter
					double t, f;                                        // conv perf double->int
					for (i = 0; i <= end + 3; i += 2)					// step 2 (odd)
						data[i] = (int)(idwtIKappa * data[i]);
					for (t=idwtDelta*data[0], i=1; i<=end+2; i+=2, t=f) // step 1&3 (even) - combined
						data[i] = (int)(idwtKappa*data[i] - t - (f=idwtDelta*data[i+1]));
					for (t=idwtGamma*data[1], i=2; i<=end+1; i+=2, t=f) // step 4 (odd)
						data[i] -= (int)(t + (f = idwtGamma*data[i+1]));
					for (t=idwtBeta*data[2], i=3; i<=end; i+=2, t=f)    // step 5 (even)
						data[i] -= (int)(t + (f = idwtBeta*data[i+1]));
					for (t=idwtAlpha*data[3], i=4; i<=end-1; i+=2, t=f) // step 6 (odd)
						data[i] -= (int)(t + (f = idwtAlpha*data[i+1]));
				}
				else {													// 5-3 reversible filter
					for (i = 3; i <= end; i += 2)						// step 1 (even)
						data[i] -= (data[i-1] + data[i+1] + 2) >> 2;
					for (i = 4; i < end; i += 2)						// step 2 (odd)
						data[i] += (data[i-1] + data[i+1]) >> 1;
				}
			}
			else if (offset == 4)
				data[0] >>= 1;
		}
		void InverseMultiCompAndDC(JPXTile tile) {
			int t, j, x, y, iData;
			if (tile.multiComp == 1) {                  //----- inverse multi-component transform
				if (img.nComps < 3
				|| tile.tileComps[0].hSep != tile.tileComps[1].hSep
				|| tile.tileComps[0].vSep != tile.tileComps[1].vSep
				|| tile.tileComps[1].hSep != tile.tileComps[2].hSep
				|| tile.tileComps[1].vSep != tile.tileComps[2].vSep)
					return;
				int[] b0 = tile.tileComps[0].data, b1 = tile.tileComps[1].data, b2 = tile.tileComps[2].data;
				bool irr = tile.tileComps[0].transform == 0;
				for (j = y = 0; y < tile.tileComps[0].h; ++y)
					for (x = 0; x < tile.tileComps[0].w; ++x, ++j) {
						int d0 = b0[j], d1 = b1[j], d2 = b2[j];
						if (irr) { // inverse irreversible multiple component transform
							b0[j] = (int)(d0 + 1.402 * d2 + 0.5);
							b1[j] = (int)(d0 - 0.34413 * d1 - 0.71414 * d2 + 0.5);
							b2[j] = (int)(d0 + 1.772 * d1 + 0.5);
						}
						else {
							b1[j] = t = d0 - ((d2 + d1) >> 2);
							b0[j] = d2 + t;
							b2[j] = d1 + t;
						}
					}
			}
			//----- DC level shift
			for (int comp = 0; comp < img.nComps; ++comp) {
				JPXTileComp tileComp = tile.tileComps[comp];
				int[] pData = tileComp.data;
				if (tileComp.sgned) {           // signed: clip
					int minVal = -(1 << (tileComp.prec - 1));
					int maxVal = (1 << (tileComp.prec - 1)) - 1;
					for (iData = y = 0; y < tileComp.h; ++y)
						for (x = 0; x < tileComp.w; ++x, ++iData) {
							int coeff = pData[iData];
							if (tileComp.transform == 0)
								coeff >>= fracBits - tileComp.prec;
							pData[iData] = coeff<minVal ? minVal : (coeff>maxVal ? maxVal : coeff);
						}
				}
				else {                      // unsigned: inverse DC level shift and clip
					int maxVal = (1 << tileComp.prec) - 1;
					int zeroVal = 1 << (tileComp.prec - 1);
					int shift = fracBits - tileComp.prec;
					int rounding = 1 << (shift - 1);
					for (iData = y = 0; y < tileComp.h; ++y)
						for (x = 0; x < tileComp.w; ++x, ++iData) {
							int coeff = pData[iData];
							if (tileComp.transform == 0)
								coeff = (coeff + rounding) >> shift;
							pData[iData] = Math.Min(Math.Max(coeff + zeroVal, 0), maxVal);
						}
				}
			}
		}
		bool ReadBoxHdr(ref int boxType, ref int boxLen, ref int dataLen) {
			(boxLen, boxType) = (bufStr.ReadInt(4), bufStr.ReadInt(4));
			if (boxLen == 1) {
				bufStr.Position += 4;
				boxLen = bufStr.ReadInt(4);
				dataLen = boxLen - 16;
			}
			else
				dataLen = (boxLen == 0) ? 0 : boxLen - 8;
			return boxType != -1;
		}
		void ReadMarkerHdr(ref int segType, ref int segLen) {
			for (segType = 0; segType == 0; ) {
				for (; (segType = bufStr.ReadByte()) != -1 && segType != 0xff;)
					;
				for (; (segType = bufStr.ReadByte()) == 0xff;)
					;
			}
			segLen = (segType == 0x4f || segType == 0x92 || segType == 0x93 || segType == 0xd9
						|| (segType >= 0x30 && segType <= 0x3f)) ? 0 : bufStr.ReadInt(2);
			if (segType == -1 || segLen == -1)
				Error("Error in JPX tile-part codestream");
		}
		void StartBitBuf(int byteCountA) {
			bitBufLen = 0;
			bitBufSkip = false;
			byteCount = byteCountA;
		}
		bool ReadBits(int nBits, ref int x) {
			for (int c; bitBufLen < nBits; bitBufSkip = c == 0xff) {
				if (byteCount == 0 || (c = bufStr.ReadByte()) == -1)
					return false;
				--byteCount;
				if (bitBufSkip) {
					bitBuf = (bitBuf << 7) | (c & 0x7f);
					bitBufLen += 7;
				}
				else {
					bitBuf = (bitBuf << 8) | (c & 0xff);
					bitBufLen += 8;
				}
			}
			x = (bitBuf >> (bitBufLen - nBits)) & ((1 << nBits) - 1);
			bitBufLen -= nBits;
			return true;
		}
		void SkipSOP() {
			// SOP occurs at the start of the packet header, so we don't need to
			// worry about bit-stuff prior to it
			var pos = bufStr.Position;
			int b1 = bufStr.ReadByte(), b2 = bufStr.ReadByte();
			bufStr.Position = pos;
			if (byteCount >= 6 && b1 == 0xff && b2 == 0x91) {
				bufStr.Position += 6;
				byteCount -= 6;
				bitBufLen = 0;
				bitBufSkip = false;
			}
		}
		void SkipEPH() {
			int k = bitBufSkip ? 1 : 0;
			var pos = bufStr.Position;
			byte[] buf = new byte[k + 2];
			bufStr.Read(buf, 0, buf.Length);
			bufStr.Position = pos;
			if (byteCount >= k + 2 && buf[k] == 0xff && buf[k + 1] == 0x92) {
				bufStr.Position += k + 2;
				byteCount -= k + 2;
				bitBufLen = 0;
				bitBufSkip = false;
			}
		}
		int FinishBitBuf() {
			if (bitBufSkip) {
				bufStr.ReadByte();
				--byteCount;
			}
			return byteCount;
		}
		int nComps = 0;                     // number of components
		JPXPalette palette = new JPXPalette(); // the palette
		bool havePalette = false;           // set if a palette has been found
		JPXImage img = new JPXImage();		// JPEG2000 decoder data
		int bitBuf = 0;						// buffer for bit reads
		int bitBufLen = 0;					// number of bits in bitBuf
		int byteCount = 0;					// number of available bytes left
		bool bitBufSkip = false;            // true if next bit should be skipped (for bit stuffing)
	}
	public class PBoxJBig2 : XPdfStream {
		static int GetMaxBit(int n) {
			return (int)Math.Ceiling(Math.Log(n) / Math.Log(2));
		}
		internal class JB2Bmp {
			internal int height, width, stride;
			internal byte[] bitmap;
			internal JB2Bmp(int w, int h, bool fillOne = false) {
				(height, width, stride) = (h, w, (w + 7) >> 3);
				bitmap = new byte[h * stride];
				if (fillOne)
					for (int i = bitmap.Length - 1; i >= 0; i--)
						bitmap[i] = 0xff;
			}
			internal byte GetPixel(int x, int y) {
				if (x < 0 || x >= width)	return 0;
				if (y < 0 || y >= height)	return 0;
				int idx = GetIdx(x, y), bit = x & 0x07, toShift = 7 - bit;
				return (byte)((bitmap[idx] >> toShift) & 0x01);
			}
			internal void SetPixel(int x, int y, byte pix) {
				int idx = GetIdx(x, y), bit = x & 0x07, shift = 7 - bit;
				bitmap[idx] = (byte)(bitmap[idx] | (pix << shift));
			}
			internal int GetIdx(int x, int y) { return y * stride + (x >> 3); }
			internal int GetInt(int index) { return bitmap[index] & 0xff; }
			internal int Length => bitmap.Length;
			internal JB2Bmp Extract(Rectangle roi) {
				JB2Bmp dst = new JB2Bmp(roi.Width, roi.Height);
				int upShift = roi.X & 0x07, downShift = 8 - upShift, dstLnStart = 0,
					srcLnStart = GetIdx(roi.X, roi.Y), pad = 8 - dst.width & 0x07,
					srcLnEnd = GetIdx(roi.X + roi.Width - 1, roi.Y);
				bool usePadding = dst.stride == srcLnEnd + 1 - srcLnStart;
				for (int y = roi.Y; y < roi.Bottom; y++) {
					int srcIdx = srcLnStart, dstIdx = dstLnStart;
					if (srcLnStart == srcLnEnd) {
						byte pixels = (byte)(bitmap[srcIdx] << upShift);
						dst.bitmap[dstIdx] = Unpad(pad, pixels);
					}
					else if (upShift == 0)
						for (int x = srcLnStart; x <= srcLnEnd; x++) {
							byte value = bitmap[srcIdx++];
							if (x == srcLnEnd && usePadding)
								value = Unpad(pad, value);
							dst.bitmap[dstIdx++] = value;
						}
					else {
						for (int x = srcLnStart; x < srcLnEnd; x++)
							if (srcIdx + 1 < Length) {
								bool isLastByte = x + 1 == srcLnEnd;
								byte value = (byte)((uint)bitmap[srcIdx++] << upShift
												   | ((uint)bitmap[srcIdx] & 0xff) >> downShift);
								if (isLastByte && !usePadding)
									value = Unpad(pad, value);
								dst.bitmap[dstIdx++] = value;
								if (isLastByte && usePadding) {
									value = Unpad(pad, (byte)((bitmap[srcIdx] & 0xff) << upShift));
									dst.bitmap[dstIdx] = value;
								}
							}
							else {
								byte value = (byte)(bitmap[srcIdx++] << upShift & 0xff);
								dst.bitmap[dstIdx++] = value;
							}
					}
					srcLnStart	+= stride;
					srcLnEnd	+= stride;
					dstLnStart	+= dst.stride;
				}
				return dst;
			}
			static byte Unpad(int padding, byte value) {
				return (byte)((uint)value >> padding << padding);
			}
			internal static byte CombineBytes(byte value1, byte value2, ComboOper op) {
				switch (op) {
					case ComboOper.OR:		return (byte)(value2 | value1);
					case ComboOper.AND:		return (byte)(value2 & value1);
					case ComboOper.XOR:		return (byte)(value2 ^ value1);
					case ComboOper.XNOR:	return (byte)(~(value1 ^ value2));
					case ComboOper.REPLACE:
					default:				return value2;
				}
			}
			internal void blit(JB2Bmp dst, int x, int y, ComboOper op) {
				int lnStart = 0, srcStart = 0, srcEnd = stride - 1;
				if (x < 0) {
					srcStart = -x;
					x = 0;
				}
				else if (x + width > dst.width)
					srcEnd -= (width + x - dst.width);
				if (y < 0) {
					lnStart = -y;
					y = 0;
					srcStart += stride;
					srcEnd += stride;
				}
				else if (y + height > dst.height)
					lnStart = height + y - dst.height;
				int shiftVal1 = x & 0x07, shiftVal2 = 8 - shiftVal1,
					padding = width & 0x07, toShift = shiftVal2 - padding,
					dstStart = dst.GetIdx(x, y),
					lastLine = Math.Min(height, lnStart + dst.height);
				bool useShift = (shiftVal2 & 0x07) != 0;
				bool specialCase = width <= ((srcEnd - srcStart) << 3) + shiftVal2;
				if (!useShift) {
					int length = srcEnd - srcStart + 1; // srcEndIdx is inclusive
					int srcStartOffset = srcStart, dstStartOffset = dstStart;
					for (int lines = lastLine - lnStart; lines > 0; lines--,
							srcStartOffset += stride, dstStartOffset += dst.stride) {
						int srcIdx = srcStartOffset, dstIdx = dstStartOffset, count = length;
						if (ComboOper.REPLACE == op)
							Array.Copy(bitmap, srcIdx, dst.bitmap, dstIdx, count);
						else
							while (count-- > 0)
								dst.bitmap[dstIdx] = CombineBytes(bitmap[srcIdx++], dst.bitmap[dstIdx++], op);
					}
				}
				else if (specialCase)
					for (int dstLine = lnStart; dstLine < lastLine; dstLine++,
							dstStart += dst.stride, srcStart += stride, srcEnd += stride) {
						int reg = 0, dstIdx = dstStart;
						for (int srcIdx = srcStart; srcIdx <= srcEnd; srcIdx++) {
							reg = (reg | bitmap[srcIdx] & 0xff) << shiftVal2;
							byte oldByte = dst.bitmap[dstIdx], newByte = (byte)(reg >> 8);
							if (srcIdx == srcEnd)
								newByte = Unpad(toShift, newByte);
							dst.bitmap[dstIdx++] = CombineBytes(oldByte, newByte, op);
							reg <<= shiftVal1;
						}
					}
				else
					for (int dstLine = lnStart; dstLine < lastLine; dstLine++, dstStart += dst
							.stride, srcStart += stride, srcEnd += stride) {
						int reg = 0, dstIdx = dstStart;
						for (int srcIdx = srcStart; srcIdx <= srcEnd; srcIdx++) {
							reg = (reg | bitmap[srcIdx] & 0xff) << shiftVal2;
							byte oldByte = dst.bitmap[dstIdx], newByte = (byte)((uint)reg >> 8);
							dst.bitmap[dstIdx++] = CombineBytes(oldByte, newByte, op);
							reg <<= shiftVal1;
							if (srcIdx == srcEnd) {
								newByte = (byte)((uint)reg >> (8 - shiftVal2));
								if (padding != 0)
									newByte = Unpad(8 + toShift, newByte);
								oldByte = dst.bitmap[dstIdx];
								dst.bitmap[dstIdx] = CombineBytes(oldByte, newByte, op);
							}
						}
					}
			}
			internal Image ToImage() {
				Bitmap bmp = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
				var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
							ImageLockMode.ReadWrite, PixelFormat.Format1bppIndexed);
				byte[] tmp = new byte[stride];
				for (int y = 0, len = tmp.Length; y < bmp.Height; y++) {
					Array.Clear(tmp, 0, tmp.Length);
					for (int x = 0; x < len; x++)
						tmp[x] = (byte)~bitmap[y * len + x];
					Marshal.Copy(tmp, 0, bd.Scan0 + y * bd.Stride, tmp.Length);
				}
				bmp.UnlockBits(bd);
				return bmp;
			}
		}
		internal class HuffmanTable {
			internal class Code {
				internal int prefixLen, rngLen, rngLow, code = -1;
				internal bool isLowRng;
				internal Code(int prefixLength, int rangeLength, int rangeLow, bool isLowerRange) {
					(prefixLen, rngLen, rngLow, isLowRng)
						= (prefixLength, rangeLength, rangeLow, isLowerRange);
				}
				internal Code() {
				}
				internal Code(Code oth) :
					this(oth.prefixLen, oth.rngLen, oth.rngLow, oth.isLowRng) { }
				internal virtual long Decode(ImgStream iis) { return long.MaxValue; }
			}
			internal class InternalNode : Code {
				int depth;
				Code zero, one;
				internal InternalNode(int depth = 0) {
					this.depth = depth;
				}
				internal void Append(Code c) {
					if (c.prefixLen == 0)            // ignore unused codes
						return;
					int shift = c.prefixLen - 1 - depth, bit = (c.code >> shift) & 1;
					if (shift < 0)
						throw new Exception("Negative shifting is not possible.");
					if (bit == 1) SetNode(ref one, c, shift);
					else SetNode(ref zero, c, shift);
				}
				void SetNode(ref Code dst, Code c, int shift) {
					Code n = null;
					if (shift == 0)
						n = (c.rngLen == -1) ? new Code(c) : new ValueNode(c);
					else {
						if (dst == null) n = new InternalNode(depth + 1);
						((InternalNode)(n ?? dst)).Append(c);
					}
					if (n != null && dst != null)
						throw new Exception("Node already have a value");
					dst = dst ?? n;
				}
				internal override long Decode(ImgStream iis) {
					return (iis.ReadBits(1) == 0 ? zero : one).Decode(iis);
				}
			}
			internal class ValueNode : Code {
				internal ValueNode(Code c) : base(c) { }
				internal override long Decode(ImgStream iis) {
					return rngLow + (isLowRng ? -iis.ReadBits(rngLen) : iis.ReadBits(rngLen));
				}
			}
			static readonly int[][][] TABLES = new int[][][] {	// B1
				new int[][] {
					new int[] { 1, 4, 0 }, new int[] { 2, 8, 16 }, new int[] { 3, 16, 272 }, new int[] { 3, 32, 65808 }
				},  new int[][] {	// B2
						new int[]{ 1, 0, 0 }, new int[]{ 2, 0, 1 }, new int[]{ 3, 0, 2 }, new int[]{ 4, 3, 3 }, new int[]{ 5, 6, 11 }, new int[]{ 6, 32, 75 }, new int[]{ 6, -1, 0 }
				}, new int[][] { // B3
						new int[]{ 8, 8, -256 }, new int[]{ 1, 0, 0 }, new int[]{ 2, 0, 1 }, new int[]{ 3, 0, 2 }, new int[]{ 4, 3, 3 }, new int[]{ 5, 6, 11 }, new int[]{ 8, 32, -257, 999 }, new int[]{ 7, 32, 75 }, new int[]{ 6, -1, 0 }
				}, new int[][] { // B4
            			new int[]{ 1, 0, 1 }, new int[]{ 2, 0, 2 }, new int[]{ 3, 0, 3 }, new int[]{ 4, 3, 4 }, new int[]{ 5, 6, 12 }, new int[]{ 5, 32, 76 }
				}, new int[][] { // B5
            			new int[]{ 7, 8, -255 }, new int[]{ 1, 0, 1 }, new int[]{ 2, 0, 2 }, new int[]{ 3, 0, 3 }, new int[]{ 4, 3, 4 }, new int[]{ 5, 6, 12 }, new int[]{ 7, 32, -256, 999 }, new int[]{ 6, 32, 76 }
				}, new int[][] { // B6
						new int[]{ 5, 10, -2048 }, new int[]{ 4, 9, -1024 }, new int[]{ 4, 8, -512 }, new int[]{ 4, 7, -256 }, new int[]{ 5, 6, -128 }, new int[]{ 5, 5, -64 }, new int[]{ 4, 5, -32 }, new int[]{ 2, 7, 0 }, new int[]{ 3, 7, 128 }, new int[]{ 3, 8, 256 }, new int[]{ 4, 9, 512 }, new int[]{ 4, 10, 1024 }, new int[]{ 6, 32, -2049, 999 }, new int[]{ 6, 32, 2048 }
				}, new int[][] { // B7
						new int[]{ 4, 9, -1024 }, new int[]{ 3, 8, -512 }, new int[]{ 4, 7, -256 }, new int[]{ 5, 6, -128 }, new int[]{ 5, 5, -64 }, new int[]{ 4, 5, -32 }, new int[]{ 4, 5, 0 }, new int[]{ 5, 5, 32 }, new int[]{ 5, 6, 64 }, new int[]{ 4, 7, 128 }, new int[]{ 3, 8, 256 }, new int[]{ 3, 9, 512 }, new int[]{ 3, 10, 1024 }, new int[]{ 5, 32, -1025, 999 }, new int[]{ 5, 32, 2048 }
				}, new int[][] { // B8
						new int[]{ 8, 3, -15 }, new int[]{ 9, 1, -7 }, new int[]{ 8, 1, -5 }, new int[]{ 9, 0, -3 }, new int[]{ 7, 0, -2 }, new int[]{ 4, 0, -1 }, new int[]{ 2, 1, 0 }, new int[]{ 5, 0, 2 }, new int[]{ 6, 0, 3 }, new int[]{ 3, 4, 4 }, new int[]{ 6, 1, 20 }, new int[]{ 4, 4, 22 }, new int[]{ 4, 5, 38 }, new int[]{ 5, 6, 70 }, new int[]{ 5, 7, 134 }, new int[]{ 6, 7, 262 }, new int[]{ 7, 8, 390 }, new int[]{ 6, 10, 646 }, new int[]{ 9, 32, -16, 999 }, new int[]{ 9, 32, 1670 }, new int[]{ 2, -1, 0 }
				}, new int[][] { // B9
            			new int[]{ 8, 4, -31 }, new int[]{ 9, 2, -15 }, new int[]{ 8, 2, -11 }, new int[]{ 9, 1, -7 }, new int[]{ 7, 1, -5 }, new int[]{ 4, 1, -3 }, new int[]{ 3, 1, -1 }, new int[]{ 3, 1, 1 }, new int[]{ 5, 1, 3 }, new int[]{ 6, 1, 5 }, new int[]{ 3, 5, 7 }, new int[]{ 6, 2, 39 }, new int[]{ 4, 5, 43 }, new int[]{ 4, 6, 75 }, new int[]{ 5, 7, 139 }, new int[]{ 5, 8, 267 }, new int[]{ 6, 8, 523 }, new int[]{ 7, 9, 779 }, new int[]{ 6, 11, 1291 }, new int[]{ 9, 32, -32, 999 }, new int[]{ 9, 32, 3339 }, new int[]{ 2, -1, 0 }
				}, new int[][] { // B10
            			new int[]{ 7, 4, -21 }, new int[]{ 8, 0, -5 }, new int[]{ 7, 0, -4 }, new int[]{ 5, 0, -3 }, new int[]{ 2, 2, -2 }, new int[]{ 5, 0, 2 }, new int[]{ 6, 0, 3 }, new int[]{ 7, 0, 4 }, new int[]{ 8, 0, 5 }, new int[]{ 2, 6, 6 }, new int[]{ 5, 5, 70 }, new int[]{ 6, 5, 102 }, new int[]{ 6, 6, 134 }, new int[]{ 6, 7, 198 }, new int[]{ 6, 8, 326 }, new int[]{ 6, 9, 582 }, new int[]{ 6, 10, 1094 }, new int[]{ 7, 11, 2118 }, new int[]{ 8, 32, -22, 999 }, new int[]{ 8, 32, 4166 }, new int[]{ 2, -1, 0 }
				}, new int[][] { // B11
            			new int[]{ 1, 0, 1 }, new int[]{ 2, 1, 2 }, new int[]{ 4, 0, 4 }, new int[]{ 4, 1, 5 }, new int[]{ 5, 1, 7 }, new int[]{ 5, 2, 9 }, new int[]{ 6, 2, 13 }, new int[]{ 7, 2, 17 }, new int[]{ 7, 3, 21 }, new int[]{ 7, 4, 29 }, new int[]{ 7, 5, 45 }, new int[]{ 7, 6, 77 }, new int[]{ 7, 32, 141 }
				}, new int[][] { // B12
            			new int[]{ 1, 0, 1 }, new int[]{ 2, 0, 2 }, new int[]{ 3, 1, 3 }, new int[]{ 5, 0, 5 }, new int[]{ 5, 1, 6 }, new int[]{ 6, 1, 8 }, new int[]{ 7, 0, 10 }, new int[]{ 7, 1, 11 }, new int[]{ 7, 2, 13 }, new int[]{ 7, 3, 17 }, new int[]{ 7, 4, 25 }, new int[]{ 8, 5, 41 }, new int[]{ 8, 32, 73 } //
				}, new int[][] { // B13
            			new int[]{ 1, 0, 1 }, new int[]{ 3, 0, 2 }, new int[]{ 4, 0, 3 }, new int[]{ 5, 0, 4 }, new int[]{ 4, 1, 5 }, new int[]{ 3, 3, 7 }, new int[]{ 6, 1, 15 }, new int[]{ 6, 2, 17 }, new int[]{ 6, 3, 21 }, new int[]{ 6, 4, 29 }, new int[]{ 6, 5, 45 }, new int[]{ 7, 6, 77 }, new int[]{ 7, 32, 141 }
				}, new int[][] { // B14
            			new int[]{ 3, 0, -2 }, new int[]{ 3, 0, -1 }, new int[]{ 1, 0, 0 }, new int[]{ 3, 0, 1 }, new int[]{ 3, 0, 2 } //
				}, new int[][] { // B15
						new int[]{ 7, 4, -24 }, new int[]{ 6, 2, -8 }, new int[]{ 5, 1, -4 }, new int[]{ 4, 0, -2 }, new int[]{ 3, 0, -1 }, new int[]{ 1, 0, 0 }, new int[]{ 3, 0, 1 }, new int[]{ 4, 0, 2 }, new int[]{ 5, 1, 3 }, new int[]{ 6, 2, 5 }, new int[]{ 7, 4, 9 }, new int[]{ 7, 32, -25, 999 }, new int[]{ 7, 32, 25 }
			} };
			static HuffmanTable[] STANDARD_TABLES = new HuffmanTable[TABLES.Length];
			InternalNode rootNode = new InternalNode();
			internal long Decode(ImgStream iis) {
				return rootNode.Decode(iis);
			}
			public HuffmanTable(List<Code> codes = null) {
				if (codes == null) return;
				int maxPrefixLength = codes.Max(x => x.prefixLen); /* Annex B.3 1) - build the histogram */
				int[] lenCount = new int[maxPrefixLength + 1];
				foreach (Code c in codes)
					lenCount[c.prefixLen]++;
				int[] code1 = new int[lenCount.Length + 1];
				lenCount[0] = 0;							      /* Annex B.3 3) */
				for (int curLen = 1; curLen <= lenCount.Length; curLen++) {
					code1[curLen] = (code1[curLen - 1] + (lenCount[curLen - 1]) << 1);
					int curCode = code1[curLen];
					foreach (Code code in codes)
						if (code.prefixLen == curLen)
							code.code = curCode++;
				}
				foreach (Code c in codes)
					rootNode.Append(c);
			}
			public static HuffmanTable GetStdTbl(int number) {
				HuffmanTable table = STANDARD_TABLES[number - 1];
				if (table == null) {
					var iniVals = TABLES[number - 1].Select(x => new Code(x[0], x[1], x[2], x.Length > 3));
					STANDARD_TABLES[number - 1] = table = new HuffmanTable(iniVals.ToList());
				}
				return table;
			}
		}
		internal class MMRDecompressor {
			const int Lvl1TblSize = 8, Lvl1Mark = (1 << Lvl1TblSize) - 1, CodeOffset = 24,
						Lvl2TblSize = 5, Lbl2Mask = (1 << Lvl2TblSize) - 1;
			const int EOF = -3, INVALID = -2, EOL = -1, CODE_P = 0, CODE_H = 1, CODE_V0 = 2,
				CODE_VR1 = 3, CODE_VR2 = 4, CODE_VR3 = 5, CODE_VL1 = 6, CODE_VL2 = 7, CODE_VL3 = 8,
				CODE_EXT2D = 9, CODE_EXT1D = 10;
			// --------------------------------------------------------------------------------------------------------------
			static readonly int[][] ModeCodes = new int[][] {
				new int[] { 4, 0x1, CODE_P }, new int[] { 3, 0x1, CODE_H }, new int[] { 1, 0x1, CODE_V0 },
				new int[] { 3, 0x3, CODE_VR1 }, new int[] { 6, 0x3, CODE_VR2 }, new int[] { 7, 0x3, CODE_VR3 },
				new int[] { 3, 0x2, CODE_VL1 }, new int[] { 6, 0x2, CODE_VL2 }, new int[] { 7, 0x2, CODE_VL3 },
				new int[] { 10, 0xf, CODE_EXT2D }, new int[] { 12, 0xf, CODE_EXT1D }, new int[] { 12, 0x1, EOL }
			};
			static readonly int[][] WhiteCodes = {
				new int[] { 4, 0x07, 2 }, new int[] { 4, 0x08, 3 }, new int[] { 4, 0x0B, 4 }, new int[] { 4, 0x0C, 5 },
				new int[] { 4, 0x0E, 6 }, new int[] { 4, 0x0F, 7 }, new int[] { 5, 0x12, 128 }, new int[] { 5, 0x13, 8 },
				new int[] { 5, 0x14, 9 }, new int[] { 5, 0x1B, 64 }, new int[] { 5, 0x07, 10 }, new int[] { 5, 0x08, 11 },
				new int[] { 6, 0x17, 192 }, new int[] { 6, 0x18, 1664 }, new int[] { 6, 0x2A, 16 }, new int[] { 6, 0x2B, 17 },
				new int[] { 6, 0x03, 13 }, new int[] { 6, 0x34, 14 }, new int[] { 6, 0x35, 15 }, new int[] { 6, 0x07, 1 },
				new int[] { 6, 0x08, 12 }, new int[] { 7, 0x13, 26 }, new int[] { 7, 0x17, 21 }, new int[] { 7, 0x18, 28 },
				new int[] { 7, 0x24, 27 }, new int[] { 7, 0x27, 18 }, new int[] { 7, 0x28, 24 }, new int[] { 7, 0x2B, 25 },
				new int[] { 7, 0x03, 22 }, new int[] { 7, 0x37, 256 }, new int[] { 7, 0x04, 23 }, new int[] { 7, 0x08, 20 },
				new int[] { 7, 0xC, 19 }, new int[] { 8, 0x12, 33 }, new int[] { 8, 0x13, 34 }, new int[] { 8, 0x14, 35 },
				new int[] { 8, 0x15, 36 }, new int[] { 8, 0x16, 37 }, new int[] { 8, 0x17, 38 }, new int[] { 8, 0x1A, 31 },
				new int[] { 8, 0x1B, 32 }, new int[] { 8, 0x02, 29 }, new int[] { 8, 0x24, 53 }, new int[] { 8, 0x25, 54 },
				new int[] { 8, 0x28, 39 }, new int[] { 8, 0x29, 40 }, new int[] { 8, 0x2A, 41 }, new int[] { 8, 0x2B, 42 },
				new int[] { 8, 0x2C, 43 }, new int[] { 8, 0x2D, 44 }, new int[] { 8, 0x03, 30 }, new int[] { 8, 0x32, 61 },
				new int[] { 8, 0x33, 62 }, new int[] { 8, 0x34, 63 }, new int[] { 8, 0x35, 0 }, new int[] { 8, 0x36, 320 },
				new int[] { 8, 0x37, 384 }, new int[] { 8, 0x04, 45 }, new int[] { 8, 0x4A, 59 }, new int[] { 8, 0x4B, 60 },
				new int[] { 8, 0x5, 46 }, new int[] { 8, 0x52, 49 }, new int[] { 8, 0x53, 50 }, new int[] { 8, 0x54, 51 },
				new int[] { 8, 0x55, 52 }, new int[] { 8, 0x58, 55 }, new int[] { 8, 0x59, 56 }, new int[] { 8, 0x5A, 57 },
				new int[] { 8, 0x5B, 58 }, new int[] { 8, 0x64, 448 }, new int[] { 8, 0x65, 512 }, new int[] { 8, 0x67, 640 },
				new int[] { 8, 0x68, 576 }, new int[] { 8, 0x0A, 47 }, new int[] { 8, 0x0B, 48 }, new int[] { 9, 0x01, INVALID },
				new int[] { 9, 0x98, 1472 }, new int[] { 9, 0x99, 1536 }, new int[] { 9, 0x9A, 1600 }, new int[] { 9, 0x9B, 1728 },
				new int[] { 9, 0xCC, 704 }, new int[] { 9, 0xCD, 768 }, new int[] { 9, 0xD2, 832 }, new int[] { 9, 0xD3, 896 },
				new int[] { 9, 0xD4, 960 }, new int[] { 9, 0xD5, 1024 }, new int[] { 9, 0xD6, 1088 }, new int[] { 9, 0xD7, 1152 },
				new int[] { 9, 0xD8, 1216 }, new int[] { 9, 0xD9, 1280 }, new int[] { 9, 0xDA, 1344 }, new int[] { 9, 0xDB, 1408 },
				new int[] { 10, 0x01, INVALID }, new int[] { 11, 0x01, INVALID }, new int[] { 11, 0x08, 1792 },
				new int[] { 11, 0x0C, 1856 }, new int[] { 11, 0x0D, 1920 }, new int[] { 12, 0x00, EOF },
				new int[] { 12, 0x01, EOL }, new int[] { 12, 0x12, 1984 }, new int[] { 12, 0x13, 2048 },
				new int[] { 12, 0x14, 2112 }, new int[] { 12, 0x15, 2176 }, new int[] { 12, 0x16, 2240 },
				new int[] { 12, 0x17, 2304 }, new int[] { 12, 0x1C, 2368 }, new int[] { 12, 0x1D, 2432 },
				new int[] { 12, 0x1E, 2496 }, new int[] { 12, 0x1F, 2560 }
			};
			static readonly int[][] BlackCodes = {
				new int[] { 2, 0x02, 3 }, new int[] { 2, 0x03, 2 }, new int[] { 3, 0x02, 1 }, new int[] { 3, 0x03, 4 },
				new int[] { 4, 0x02, 6 }, new int[] { 4, 0x03, 5 }, new int[] { 5, 0x03, 7 }, new int[] { 6, 0x04, 9 },
				new int[] { 6, 0x05, 8 }, new int[] { 7, 0x04, 10 }, new int[] { 7, 0x05, 11 }, new int[] { 7, 0x07, 12 },
				new int[] { 8, 0x04, 13 }, new int[] { 8, 0x07, 14 }, new int[] { 9, 0x01, INVALID }, new int[] { 9, 0x18, 15 },
				new int[] { 10, 0x01, INVALID }, new int[] { 10, 0x17, 16 }, new int[] { 10, 0x18, 17 }, new int[] { 10, 0x37, 0 },
				new int[] { 10, 0x08, 18 }, new int[] { 10, 0x0F, 64 }, new int[] { 11, 0x01, INVALID }, new int[] { 11, 0x17, 24 },
				new int[] { 11, 0x18, 25 }, new int[] { 11, 0x28, 23 }, new int[] { 11, 0x37, 22 }, new int[] { 11, 0x67, 19 },
				new int[] { 11, 0x68, 20 }, new int[] { 11, 0x6C, 21 }, new int[] { 11, 0x08, 1792 }, new int[] { 11, 0x0C, 1856 },
				new int[] { 11, 0x0D, 1920 }, new int[] { 12, 0x00, EOF }, new int[] { 12, 0x01, EOL }, new int[] { 12, 0x12, 1984 },
				new int[] { 12, 0x13, 2048 }, new int[] { 12, 0x14, 2112 }, new int[] { 12, 0x15, 2176 }, new int[] { 12, 0x16, 2240 },
				new int[] { 12, 0x17, 2304 }, new int[] { 12, 0x1C, 2368 }, new int[] { 12, 0x1D, 2432 }, new int[] { 12, 0x1E, 2496 },
				new int[] { 12, 0x1F, 2560 }, new int[] { 12, 0x24, 52 }, new int[] { 12, 0x27, 55 }, new int[] { 12, 0x28, 56 },
				new int[] { 12, 0x2B, 59 }, new int[] { 12, 0x2C, 60 }, new int[] { 12, 0x33, 320 }, new int[] { 12, 0x34, 384 },
				new int[] { 12, 0x35, 448 }, new int[] { 12, 0x37, 53 }, new int[] { 12, 0x38, 54 }, new int[] { 12, 0x52, 50 },
				new int[] { 12, 0x53, 51 }, new int[] { 12, 0x54, 44 }, new int[] { 12, 0x55, 45 }, new int[] { 12, 0x56, 46 },
				new int[] { 12, 0x57, 47 }, new int[] { 12, 0x58, 57 }, new int[] { 12, 0x59, 58 }, new int[] { 12, 0x5A, 61 },
				new int[] { 12, 0x5B, 256 }, new int[] { 12, 0x64, 48 }, new int[] { 12, 0x65, 49 }, new int[] { 12, 0x66, 62 },
				new int[] { 12, 0x67, 63 }, new int[] { 12, 0x68, 30 }, new int[] { 12, 0x69, 31 }, new int[] { 12, 0x6A, 32 },
				new int[] { 12, 0x6B, 33 }, new int[] { 12, 0x6C, 40 }, new int[] { 12, 0x6D, 41 }, new int[] { 12, 0xC8, 128 },
				new int[] { 12, 0xC9, 192 }, new int[] { 12, 0xCA, 26 }, new int[] { 12, 0xCB, 27 }, new int[] { 12, 0xCC, 28 },
				new int[] { 12, 0xCD, 29 }, new int[] { 12, 0xD2, 34 }, new int[] { 12, 0xD3, 35 }, new int[] { 12, 0xD4, 36 },
				new int[] { 12, 0xD5, 37 }, new int[] { 12, 0xD6, 38 }, new int[] { 12, 0xD7, 39 }, new int[] { 12, 0xDA, 42 },
				new int[] { 12, 0xDB, 43 }, new int[] { 13, 0x4A, 640 }, new int[] { 13, 0x4B, 704 }, new int[] { 13, 0x4C, 768 },
				new int[] { 13, 0x4D, 832 }, new int[] { 13, 0x52, 1280 }, new int[] { 13, 0x53, 1344 }, new int[] { 13, 0x54, 1408 },
				new int[] { 13, 0x55, 1472 }, new int[] { 13, 0x5A, 1536 }, new int[] { 13, 0x5B, 1600 }, new int[] { 13, 0x64, 1664 },
				new int[] { 13, 0x65, 1728 }, new int[] { 13, 0x6C, 512 }, new int[] { 13, 0x6D, 576 }, new int[] { 13, 0x72, 896 },
				new int[] { 13, 0x73, 960 }, new int[] { 13, 0x74, 1024 }, new int[] { 13, 0x75, 1088 }, new int[] { 13, 0x76, 1152 },
				new int[] { 13, 0x77, 1216 }
			};
			int width, height;
			// A class encapsulating the compressed raw data.
			internal class MMRCode {
				internal MMRCode[] subTable = null;
				internal int bitLength, codeWord, runLen;
				internal MMRCode(int[] codeData) {
					(bitLength, codeWord, runLen) = (codeData[0], codeData[1], codeData[2]);
				}
			}
			static readonly MMRCode[]	whiteTable	= InitTable(WhiteCodes),
										blackTable	= InitTable(BlackCodes),
										modeTable	= InitTable(ModeCodes);
			internal class RunData {
				internal ImgStream stream;      /** Compressed data stream. */
				internal int offset, lstOffset = 0, lstCode = 0, bufBase, bufTop, iniPos, maxLen;
				byte[] buffer;
				internal RunData(ImgStream stream, long len) {
					this.stream = stream;
					iniPos = (int)stream.Position;
					lstOffset = 1;
					try {
						maxLen = (int)len;
						buffer = new byte[(int)Math.Min(Math.Max(3, len), 1 << 17)];
						FillBuffer(0);
					}
					catch (IOException e) {
						buffer = new byte[10];
						Error(e.ToString(), false);
					}
				}
				internal MMRCode UncompressGetCode(MMRCode[] table) {
					int code = UncompressGetNextCodeLittleEndian() & 0xffffff;
					MMRCode res = table[code >> CodeOffset - Lvl1TblSize];
					if (null != res && null != res.subTable)  // perform second-level lookup
						res = res.subTable[(code >> CodeOffset - Lvl1TblSize - Lvl2TblSize) & Lbl2Mask];
					return res;
				}
				// Fill up the code word in little endian mode. This is a hotspot, therefore the algorithm is heavily optimised.
				// For the frequent cases (i.e. short words) we try to get away with as little work as possible. <br>
				// This method returns code words of 16 bits, which are aligned to the 24th bit. The lowest 8 bits are used as a
				// "queue" of bits so that an access to the actual data is only needed, when this queue becomes empty.
				int UncompressGetNextCodeLittleEndian() {
					try {
						int bitsToFill = offset - lstOffset;			// the # of bits to fill (offset difference)
						if (bitsToFill < 0 || bitsToFill > 24) {		// refill at absolute offset
							int byteOffset = (offset >> 3) - bufBase;	// offset>>3 is equivalent to offset/8
							if (byteOffset >= bufTop) {
								FillBuffer(byteOffset += bufBase);
								byteOffset -= bufBase;
							}
							lstCode = (buffer[byteOffset] & 0xff) << 16
									| (buffer[byteOffset + 1] & 0xff) << 8
									| (buffer[byteOffset + 2] & 0xff);
							lstCode <<= offset & 7;						// equivalent to offset%8
						}
						else {
							int bitOffset = lstOffset & 7;		// the offset to the next byte 
							if (bitsToFill <= 7 - bitOffset)	// boundary check whether there 
								lstCode <<= bitsToFill;			// are enough bits in the "queue"
							else {
								int byteOffset = (lstOffset >> 3) + 3 - bufBase;
								if (byteOffset >= bufTop) {
									byteOffset += bufBase;
									FillBuffer(byteOffset);
									byteOffset -= bufBase;
								}
								bitOffset = 8 - bitOffset;
								do {
									lstCode <<= bitOffset;
									lstCode |= buffer[byteOffset] & 0xff;
									bitsToFill -= bitOffset;
									byteOffset++;
									bitOffset = 8;
								} while (bitsToFill >= 8);
								lstCode <<= bitsToFill;			// shift the rest
							}
						}
						lstOffset = offset;
						return lstCode;
					}
					catch (IOException e) {// will this actually happen? only with broken data, I'd say.
						throw new IndexOutOfRangeException("Corrupted RLE data caused by an IOException while reading raw data: " + e.ToString());
					}
				}
				void FillBuffer(int byteOffset) {
					bufBase = byteOffset;
					if (byteOffset < maxLen)
						try {
							stream.Seek(iniPos + byteOffset, SeekOrigin.Begin);
							bufTop = stream.Read(buffer, 0, buffer.Length - bufBase);
							for (int ml = Math.Min(3, buffer.Length); bufTop < ml; bufTop++)
								buffer[bufTop] = 0;
						}
						catch {
							bufTop = -1;                         // IDK which kind of EOF will kick in
						}                                           // check filling degree
					bufTop -= 3;                                 // leave some room, in order to save 
					if (bufTop < 0) {                            // a few tests in the calling code
						Array.Clear(buffer, 0, buffer.Length);      // if EOF, just supply zero-bytes
						bufTop = buffer.Length - 3;
					}
				}
				internal void Align() {                 // Skip to next byte
					offset = ((offset + 7) >> 3) << 3;
				}
			}
			internal RunData data;
			int Uncompress2D(RunData runData, int[] refOffsets, int refRunLen, int[] runOffsets, int width) {
				int refIdx = 0, curIdx = 0, bitPos = 0;
				bool whiteRun = true; // Always start with a white run
				MMRCode code = null; // Storage var for current code being processed
				refOffsets[refRunLen] = refOffsets[refRunLen + 1] = width;
				refOffsets[refRunLen + 2] = refOffsets[refRunLen + 3] = width + 1;
				try {
					while (bitPos < width) {
						code = runData.UncompressGetCode(modeTable);    // Get the mode code
						if (code == null) {
							runData.offset++;
							break;
						}
						runData.offset += code.bitLength;               // Add the code length to the bit offset
						switch (code.runLen) {
							case CODE_V0 : bitPos = refOffsets[refIdx];		break;
							case CODE_VR1: bitPos = refOffsets[refIdx] + 1;	break;
							case CODE_VL1: bitPos = refOffsets[refIdx] - 1;	break;
							case CODE_VR2: bitPos = refOffsets[refIdx] + 2;	break;
							case CODE_VL2: bitPos = refOffsets[refIdx] - 2;	break;
							case CODE_VR3: bitPos = refOffsets[refIdx] + 3;	break;
							case CODE_VL3: bitPos = refOffsets[refIdx] - 3;	break;
							case CODE_P:
								refIdx++;
								bitPos = refOffsets[refIdx++];
								continue;
							case CODE_H:
								for (int ever = 1; ever > 0;) {
									code = runData.UncompressGetCode(whiteRun ? whiteTable : blackTable);
									if (code == null)
										goto loopDone;
									runData.offset += code.bitLength;
									if (code.runLen < 64) {
										if (code.runLen < 0) {
											runOffsets[curIdx++] = bitPos;
											code = null;
											goto loopDone;
										}
										bitPos += code.runLen;
										runOffsets[curIdx++] = bitPos;
										break;
									}
									bitPos += code.runLen;
								}
								for (int ever1 = 1, HalfBitPos1 = bitPos; ever1 > 0;) {
									code = runData.UncompressGetCode(!whiteRun ? whiteTable : blackTable);
									if (code == null)
										goto loopDone;
									runData.offset += code.bitLength;
									if (code.runLen < 64) {
										if (code.runLen < 0) {
											runOffsets[curIdx++] = bitPos;
											goto loopDone;
										}
										bitPos += code.runLen;
										// don't generate 0-length run at EOL for cases where the line ends in an H-run.
										if (bitPos < width || bitPos != HalfBitPos1)
											runOffsets[curIdx++] = bitPos;
										break;
									}
									bitPos += code.runLen;
								}
								while (bitPos < width && refOffsets[refIdx] <= bitPos)
									refIdx += 2;
								continue;
							case EOL:
							default:        // Possibly MMR Decoded
								Error("Should not happen!", false);
								if (runData.offset == 12 && code.runLen == EOL) {
									runData.offset = 0;
									Uncompress1D(runData, refOffsets, width);
									runData.offset++;
									Uncompress1D(runData, runOffsets, width);
									int retCode = Uncompress1D(runData, refOffsets, width);
									runData.offset++;
									return retCode;
								}
								bitPos = width;
								continue;
						}
						if (bitPos <= width) { // Only vertical modes get this far
							whiteRun = !whiteRun;
							runOffsets[curIdx++] = bitPos;
							refIdx += (refIdx > 0 ? -1 : 1);
							while (bitPos < width && refOffsets[refIdx] <= bitPos)
								refIdx += 2;
						}
					}
				loopDone:
					refIdx = 0; // compiler warning
				}
				catch (Exception t) {
					Error(t.ToString(), false);
					return EOF;
				}
				if (runOffsets[curIdx] != width)
					runOffsets[curIdx] = width;
				return (code == null) ? EOL : curIdx;
			}
			internal MMRDecompressor(int width, int height, ImgStream stream, long len) {
				this.width = width;
				this.height = height;
				data = new RunData(stream, len);
			}
			internal JB2Bmp Uncompress() {
				JB2Bmp bmp = new JB2Bmp(width, height);
				int[] curOff = new int[width + 5], refOff = new int[width + 5];
				refOff[0] = width;
				int line = 0, refRunLen = 1, count;
				for (; line < height; line++, refRunLen = count) {
					if (EOF == (count = Uncompress2D(data, refOff, refRunLen, curOff, width)))
						break;
					if (count > 0) {
						int x = 0, dstIdx = bmp.GetIdx(0, line);
						byte dstVal = 0;
						for (int i = 0; i < count; i++)
							for (int off = curOff[i], val = (i & 1); x < off;) {
								dstVal = (byte)((dstVal << 1) | val);
								if ((++x & 7) == 0) {
									bmp.bitmap[dstIdx++] = dstVal;
									dstVal = 0;
								}
							}
						if ((x & 7) != 0) {
							dstVal <<= 8 - (x & 7);
							bmp.bitmap[dstIdx] = dstVal;
						}
					}
					(refOff, curOff) = (curOff, refOff);		// swap
				}
				for (MMRCode c; true; data.offset += c.bitLength) 
					if ((c = data.UncompressGetCode(modeTable)) == null || c.runLen != EOL)
						break;
				data.Align();
				return bmp;
			}
			int Uncompress1D(RunData runData, int[] runOffsets, int width) {
				bool whiteRun = true;               // should not get here !!!!!
				MMRCode code = null;
				int iBitPos = 0, refOffset = 0;
				for (; iBitPos < width; )
					while (true) {
						code = runData.UncompressGetCode(whiteRun ? whiteTable : blackTable);
						runData.offset += code.bitLength;
						if (code.runLen < 0)
							goto loop;
						iBitPos += code.runLen;
						if (code.runLen < 64) {
							whiteRun = !whiteRun;
							runOffsets[refOffset++] = iBitPos;
							break;
						}
					}
				loop:
				if (runOffsets[refOffset] != width)
					runOffsets[refOffset] = width;
				return code != null && code.runLen != EOL ? refOffset : EOL;
			}
			static MMRCode[] InitTable(int[][] codes) {
				MMRCode[] lvl1Tbl = new MMRCode[Lvl1Mark + 1];
				for (int i = 0; i < codes.Length; i++) {
					MMRCode code = new MMRCode(codes[i]);
					if (code.bitLength <= Lvl1TblSize) {
						int varLen = Lvl1TblSize - code.bitLength;
						int baseWord = code.codeWord << varLen;
						for (int variant = (1 << varLen) - 1; variant >= 0; variant--)
							lvl1Tbl[baseWord | variant] = code;
					}
					else {                  // init second level table
						int lvl1Idx = (int)(((uint)code.codeWord) >> code.bitLength - Lvl1TblSize);
						lvl1Tbl[lvl1Idx] = lvl1Tbl[lvl1Idx] ?? new MMRCode(new int[3]) { subTable = new MMRCode[Lbl2Mask+1] }; ;
						if (code.bitLength <= Lvl1TblSize + Lvl2TblSize) {
							MMRCode[] lvl2Tbl = lvl1Tbl[lvl1Idx].subTable;
							int varLen = Lvl1TblSize + Lvl2TblSize - code.bitLength;
							int baseWord = (code.codeWord << varLen) & Lbl2Mask;
							for (int variant = (1 << varLen) - 1; variant >= 0; variant--)
								lvl2Tbl[baseWord | variant] = code;
						}
						else
							throw new ArgumentException("Code table overflow in MMRDecompressor");
					}
				}
				return lvl1Tbl;
			}
		}
		internal enum ComboOper { OR = 0, AND = 1, XOR = 2, XNOR = 3, REPLACE = 4 }
		internal class SegmentHeader {
			ImgStream		imgStrm;
			SegmentData		segData;
			internal int	segNo, segType, pgAssoc;
			internal long	DataLen, DataStart;
			internal SegmentHeader[] rtSegments;
			internal SegmentHeader(PBoxJBig2 document, ImgStream sis, long offset) {
				imgStrm = sis;
				sis.Seek(offset, SeekOrigin.Begin);
				segNo = sis.ReadInt(4);
				int segFlags = sis.ReadByte();
				segType = segFlags & 0x3f;
				/* 7.2.4 Amount of referred-to segments */
				int countOfRTS = sis.ReadBits(3) & 0xf, arrayLength = 5;
				if (countOfRTS > 4) {                       /* long format */
					countOfRTS = (int)(sis.ReadBits(29) & 0xffffffff);
					arrayLength = (countOfRTS + 8) >> 3;
				}
				for (int i = 0; i < arrayLength; i++)
					sis.ReadBits(1);
				int[] rtsNumbers = new int[countOfRTS];     /* 7.2.5 Referred-to segments numbers */
				if (countOfRTS > 0) {
					int rtsSize = (segNo > 65536) ? 4 : (segNo > 256 ? 2 : 1);
					rtSegments = new SegmentHeader[countOfRTS];
					for (int i = 0; i < countOfRTS; i++)
						rtsNumbers[i] = sis.ReadInt(rtsSize);
				}
				/* 7.2.6 Segment page association (Checks how big the page association field is.) */
				pgAssoc = sis.ReadInt((segFlags & 0x40) != 0 ? 4 : 1);
				if (countOfRTS > 0) {
					JBIG2Page page = document.GetPage(pgAssoc);
					for (int i = 0; i < countOfRTS; i++)
						rtSegments[i] = null != page
							? page.GetSegment(rtsNumbers[i]) : document.GetGlobalSegment(rtsNumbers[i]);
				}
				DataLen = sis.ReadInt(4);
				DataStart = sis.Position;					// for rand get later 
				if ((int)DataLen == -1)						// overwritten in the MapStream
					DataLen = sis.Length - sis.Position;
			}
			internal SegmentData GetSegData() {
				if (null != segData)
					return segData;
				switch (segType) {
					case  0:					segData = new SymbolDictionary();	break;
					case  4: case  6: case  7:	segData = new TextRegion();			break;
					case 16:					segData = new PatternDictionary();	break;
					case 20: case 22: case 23:	segData = new HalftoneRegion();		break;
					case 36: case 38: case 39:	segData = new GenRegion();			break;
					case 40: case 42: case 43:	segData = new GenRefineRegion();	break;
					case 48:					segData = new PageInformation();	break;
					case 50:					segData = new EndOfStripe();		break;
					case 52:					segData = new SegmentData();		break;
					case 53:					segData = new Table();				break;
					default: throw new ArgumentException("No segment class for type " + segType);
				}
				segData.Init(this, new ImgStream(imgStrm, DataStart, DataLen));
				return segData;
			}
			internal HuffmanTable GetUserTable(params int[] prm) {
				int c = prm.Where(x => x == 3).Count();
				return rtSegments.Where(x => x.segType == 53).Skip(c)
					.Select(x => ((Table)x.GetSegData()).GetHuffTbl()).FirstOrDefault();
			}
		}
		internal class SegmentData {
			protected SegmentHeader segHdr;
			internal long		dataOffset, dataLength;
			internal ImgStream	imgStrm;
			internal ComboOper	comboOper;
			internal virtual void Init(SegmentHeader header, ImgStream sis) {
				imgStrm = sis;
				segHdr = header;
			}
			internal virtual List<JB2Bmp> GetDictionary() { return null; }
		}
		internal class EndOfStripe : SegmentData {
			internal int lineNum;
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				lineNum = imgStrm.ReadInt(4);
			}
		}
		internal class PageInformation : SegmentData {
			internal int width, height;
			/** Page segment flags, one byte, 7.4.8.5 */
			internal bool canModCombo, reqAuxBuf, hasRefine, isLossless, isStriped, defPix;
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				width		= imgStrm.ReadInt(4);
				height		= imgStrm.ReadInt(4);
				imgStrm.Position += 8;
				int flags = imgStrm.ReadByte();
				canModCombo = (flags & 0x40) != 0;					/* Bit 6 */
				reqAuxBuf	= (flags & 0x20) != 0;					/* Bit 5 */
				comboOper	= (ComboOper)((flags >> 3) & 0x03);		/* Bit 3-4 */
				defPix		= (flags & 0x04) != 0;					/* Bit 2 */
				hasRefine	= (flags & 0x02) != 0;					/* Bit 1 */
				isLossless	= (flags & 0x01) != 0;                  /* Bit 0 */
				isStriped	= (imgStrm.ReadInt(2) & 0x8000) != 0;	/* Bit 15, 0-14 maxStripeSize */
			}
			internal ComboOper GetComboOper(RegSegInfo ri) {
				return canModCombo ? ri.comboOper : comboOper;
			}
		}
		internal class RegSegInfo : SegmentData {
			internal int width, height, xLoc, yLoc;
			public RegSegInfo(ImgStream sis = null) {
				imgStrm = sis;
			}
			public void ParseHeader() {
				(width, height)	= (imgStrm.ReadInt(4), imgStrm.ReadInt(4));
				(xLoc,  yLoc)	= (imgStrm.ReadInt(4), imgStrm.ReadInt(4));
				comboOper = (ComboOper)(imgStrm.ReadByte() & 7);
			}
		}
		internal class Table : SegmentData {
			int htOOB, htPS, htRS, htLow, htHigh;         /** Code table flags, B.2.1, page 87 */
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadByte();
				htRS	= ((flags >> 4) & 7) + 1;			/* Bit 4-6 */
				htPS	= ((flags >> 1) & 7) + 1;			/* Bit 1-3 */
				htOOB	= flags & 1;						/* Bit 0 */
				htLow	= imgStrm.ReadInt(4);
				htHigh	= imgStrm.ReadInt(4);
			}
			internal HuffmanTable GetHuffTbl() {
				int pref, rng;
				var codeTable = new List<HuffmanTable.Code>();
				for (int c = htLow; c < htHigh; c += 1 << rng) {
					pref = imgStrm.ReadBits(htPS);
					rng = imgStrm.ReadBits(htRS);
					codeTable.Add(new HuffmanTable.Code(pref, rng, c, false));
				}
				codeTable.Add(new HuffmanTable.Code(imgStrm.ReadBits(htPS), 32, htLow - 1, true));
				codeTable.Add(new HuffmanTable.Code(imgStrm.ReadBits(htPS), 32, htHigh, false));
				if (htOOB == 1)                  /* Annex B.2 10) - out-of-band table line */
					codeTable.Add(new HuffmanTable.Code(imgStrm.ReadBits(htPS), -1, -1, false));
				return new HuffmanTable(codeTable);
			}
		}
		internal class PatternDictionary : SegmentData {
			/** Segment data structure (only necessary if MMR is used) */
			short[] gbAtX, gbAtY;
			bool isMMR;
			int hdpWidth, hdpHeight, grayMax, hdTemplate;
			List<JB2Bmp> patterns;
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadByte();
				hdTemplate	= (flags >> 1) & 0x03;		/* Bit 1-2 */
				isMMR		= (flags & 0x01) != 0;		/* Bit 0 */
				hdpWidth	= imgStrm.ReadByte();
				hdpHeight	= imgStrm.ReadByte();
				grayMax		= imgStrm.ReadInt(4);
				dataOffset	= imgStrm.Position;
				dataLength	= imgStrm.Length - dataOffset;
				if (hdpHeight < 1 || hdpWidth < 1)
					throw new ArgumentException("Width/Heigth must be greater than zero.");
			}
			internal override List<JB2Bmp> GetDictionary() {
				if (null != patterns) return patterns;
				if (!isMMR)
					if (hdTemplate == 0) {
						gbAtX = new short[4] { (short)-hdpWidth, -3, 2, -2 };
						gbAtY = new short[4] { 0, -1, -2, -2 };
					}
					else {
						gbAtX = new short[1] { (short)-hdpWidth };
						gbAtY = new short[1] { 0 };
					}
				GenRegion genReg = new GenRegion(imgStrm);				// 2)
				genReg.SetParams(this, isMMR, hdpHeight, (grayMax+1)*hdpWidth, hdTemplate, gbAtX, gbAtY);
				JB2Bmp colBmp = genReg.GetRegBmp();
				patterns = new List<JB2Bmp>(grayMax + 1);               // 3)
				for (int gray = 0; gray <= grayMax; gray++) {           // 4)
					var roi = new Rectangle(hdpWidth * gray, 0, hdpWidth, hdpHeight);
					patterns.Add(colBmp.Extract(roi));					// 4) b)
				}
				return patterns;
			}
		}
		internal class SymbolDictionary : SegmentData {
			/** Symbol dictionary flags, 7.4.2.1.1 */
			bool	isCodeCtxRetained, isCodeCtxUsed, useRefineAggr, isHuffman, sdrTemplate;
			int		amtExpSymb, amtNewSymb, amtImpSymb, amtDecodedSymb, sbSymCodeLen, sdTemplate, 
					sdHuffAggInstSel, sdHuffBMSizeSel, sdHuffDecWidthSel, sdHuffDecHeightSel;
			short[] sdATX, sdATY;               /** Symbol dictionary AT flags, 7.4.2.1.2 */
			short[] sdrATX, sdrATY;             /** Symbol dictionary refinement AT flags, 7.4.2.1.3 */
			List<JB2Bmp>	importSymbols;
			JB2Bmp[]		newSymbols;
			HuffmanTable	dhTable, dwTable, bmSizeTable, aggInstTable;
			List<JB2Bmp>	expSymbols, sbSymbols;
			ArithmDecoder	arithDecoder;
			TextRegion		txtReg;
			GenRegion		genReg;
			GenRefineRegion genRefineReg;
			internal CXStats cx, cxIADH, cxIADW, cxIAAI, cxIAEX, cxIARDX, cxIARDY, cxIADT, cxIAID;
			internal override List<JB2Bmp> GetDictionary() {    // 6.5.5 Decoding the symbol dictionary
				if (null == expSymbols) {
					if (useRefineAggr)
						sbSymCodeLen = GetSbSymCodeLen();
					if (!isHuffman) {
						if (cxIADT == null)
							(cxIADT, cxIADH, cxIADW, cxIAAI, cxIAEX, cx)
								= ( new CXStats(), new CXStats(), new CXStats(),
									new CXStats(), new CXStats(), new CXStats(65536));
						if (useRefineAggr && cxIAID == null)
							(cxIAID, cxIARDX, cxIARDY) = (new CXStats(1 << sbSymCodeLen),
										new CXStats(), new CXStats());
						arithDecoder = arithDecoder ?? new ArithmDecoder(imgStrm);
					}
					newSymbols = new JB2Bmp[amtNewSymb];					/* 6.5.5 1) */
					int[] newSymbWidths = null;								/* 6.5.5 2) */
					if (isHuffman && !useRefineAggr)
						newSymbWidths = new int[amtNewSymb];
					SetSymbolsArray();
					int clsHeight = 0;										/* 6.5.5 3) */
					for (amtDecodedSymb = 0; amtDecodedSymb < amtNewSymb;) {/* 6.5.5 4 a) */
						clsHeight += (int)(isHuffman ? DecodeHeightClassDeltaHeightWithHuffman() : arithDecoder.DecodeInt(cxIADH));
						int symbWidth = 0, totalWidth = 0, htCls1SymbIdx = amtDecodedSymb;
						for (; true; amtDecodedSymb++) {                    /* 6.5.5 4 c) */
							long difWidth = DecodeDifferenceWidth();        /* 4 c) i) */
							if (difWidth == long.MaxValue || amtDecodedSymb >= amtNewSymb)// Repeat until OOB - OOB sends a break;
								break;
							symbWidth += (int)difWidth;
							totalWidth += symbWidth;
							if (!isHuffman || useRefineAggr)                /* 4 c) ii) */
								if (!useRefineAggr) {
									genReg = genReg ?? new GenRegion(imgStrm);
									genReg.SetParams(sdTemplate,		// Parameters set according to Table 16, page 35
										sdATX, sdATY, symbWidth, clsHeight, cx, arithDecoder);
									AddSymbol(genReg);
								}
								else {
									long refAggrInst = (isHuffman) ? HuffDecodeRefAggInst() : arithDecoder.DecodeInt(cxIAAI);
									if (refAggrInst > 1)					// 6.5.8.2 2)
										DecodeThroughTextRegion(symbWidth, clsHeight, refAggrInst);
									else if (refAggrInst == 1)				// 6.5.8.2 3) refers to 6.5.8.2.2
										DecodeRefinedSymbol(symbWidth, clsHeight);
								}
							else if (isHuffman && !useRefineAggr)
								newSymbWidths[amtDecodedSymb] = symbWidth;                       /* 4 c) iii) */
						}
						if (isHuffman && !useRefineAggr) {					/* 6.5.5 4 d) */
							long bmSize = (sdHuffBMSizeSel != 0) ? HuffDecodeBmSize()/* 6.5.9 */
											: HuffmanTable.GetStdTbl(1).Decode(imgStrm);
							imgStrm.Seek(0, SeekOrigin.Current); // clear bitBuffer
							JB2Bmp htClsCollBmp = DecodeHeightClsCollBmp(bmSize, clsHeight, totalWidth);
							imgStrm.Seek(0, SeekOrigin.Current);			// clear bitBuffer
							for (int i = htCls1SymbIdx; i < amtDecodedSymb; i++) {
								int colStart = 0;
								for (int j = htCls1SymbIdx; j <= i - 1; j++)
									colStart += newSymbWidths[j];
								Rectangle roi = new Rectangle(colStart, 0, newSymbWidths[i], clsHeight);
								sbSymbols.Add(newSymbols[i] = htClsCollBmp.Extract(roi));
							}
						}
					}
					GetToExportFlags();										/* 6.5.10 1) - 5) */
				}
				return expSymbols;
			}
			long HuffDecodeRefAggInst() {
				if (sdHuffAggInstSel == 0)
					return HuffmanTable.GetStdTbl(1).Decode(imgStrm);
				if (sdHuffAggInstSel != 1)
					return 0;
				aggInstTable = aggInstTable 
					?? segHdr.GetUserTable(sdHuffDecHeightSel, sdHuffDecWidthSel, sdHuffBMSizeSel);
				return aggInstTable.Decode(imgStrm);
			}
			void DecodeThroughTextRegion(int symbWidth, int heightCls, long amtRefineAggInst) {
				if (txtReg == null) {
					txtReg = new TextRegion { imgStrm = imgStrm, regInfo = new RegSegInfo(imgStrm) };
					txtReg.SetContexts(cx, new CXStats(), new CXStats(), new CXStats(), new CXStats(), 
								   cxIAID, new CXStats(), new CXStats(), new CXStats(), new CXStats());
				}
				SetSymbolsArray();  // 6.5.8.2.4 Concatenating the array used as parameter later.
									// 6.5.8.2 2) Parameters set according to Table 17, page 36
				txtReg.SetParams(arithDecoder, isHuffman, symbWidth, heightCls, amtRefineAggInst, 
					amtImpSymb + amtDecodedSymb, sdrTemplate, sdrATX, sdrATY, sbSymbols, sbSymCodeLen);
				AddSymbol(txtReg);
			}
			void DecodeRefinedSymbol(int symWidth, int hcHeight) {
				int id, rdx, rdy;
				if (isHuffman) {
					id  = imgStrm.ReadBits(sbSymCodeLen);
					rdx = (int)HuffmanTable.GetStdTbl(15).Decode(imgStrm);
					rdy = (int)HuffmanTable.GetStdTbl(15).Decode(imgStrm);
					HuffmanTable.GetStdTbl(1).Decode(imgStrm);
					imgStrm.Seek(0, SeekOrigin.Current); // clear bitBuffer
				}
				else {
					id  = (int)arithDecoder.DecodeIAID(sbSymCodeLen, cxIAID);
					rdx = (int)arithDecoder.DecodeInt(cxIARDX);
					rdy = (int)arithDecoder.DecodeInt(cxIARDY);
				}
				SetSymbolsArray();							/* 6) */
				JB2Bmp ibo = sbSymbols[id];
				if (genRefineReg == null) {
					genRefineReg = new GenRefineRegion(imgStrm);
					if (arithDecoder == null)
						(arithDecoder, cx) = (new ArithmDecoder(imgStrm), new CXStats(65536));
				}
				genRefineReg.SetParams(cx, arithDecoder,	// Parameters as shown in Table 18, page 36
					sdrTemplate, symWidth, hcHeight, ibo, rdx, rdy, sdrATX, sdrATY);
				AddSymbol(genRefineReg);
				if (isHuffman)								/* 7) */
					imgStrm.Seek(0, SeekOrigin.Current);	// clear bitBuffer
			}   // Make sure that the processed bytes are equal to the value read in step 5 a)
			void AddSymbol(Region region) {
				sbSymbols.Add(newSymbols[amtDecodedSymb] = region.GetRegBmp());
			}
			long DecodeDifferenceWidth() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIADW);
				switch (sdHuffDecWidthSel) {
					case 0: return HuffmanTable.GetStdTbl(2).Decode(imgStrm);
					case 1: return HuffmanTable.GetStdTbl(3).Decode(imgStrm);
					case 3:	dwTable = dwTable ?? segHdr.GetUserTable(sdHuffDecHeightSel);
							return dwTable.Decode(imgStrm);
				}
				return 0;
			}
			long DecodeHeightClassDeltaHeightWithHuffman() {
				switch (sdHuffDecHeightSel) {
					case 0: return HuffmanTable.GetStdTbl(4).Decode(imgStrm);
					case 1: return HuffmanTable.GetStdTbl(5).Decode(imgStrm);
					case 3: return (dhTable = dhTable ?? segHdr.GetUserTable()).Decode(imgStrm);
				}
				return 0;
			}
			JB2Bmp DecodeHeightClsCollBmp(long bmSize, int htCls, int totWidth) {
				if (bmSize == 0) {
					JB2Bmp ret = new JB2Bmp(totWidth, htCls);
					for (int i = 0, len = ret.Length; i < len; i++)
						ret.bitmap[i] = (byte)imgStrm.ReadSByte();
					return ret;
				}
				genReg = genReg ?? new GenRegion(imgStrm);
				genReg.SetParams(imgStrm.Position, bmSize, htCls, totWidth);
				return genReg.GetRegBmp();
			}
			void GetToExportFlags() {
				int		curFlag = 0;
				long	exRunLen;
				int[]	exportFlags = new int[amtImpSymb + amtNewSymb];
				for (int expIdx = 0; expIdx < amtImpSymb + amtNewSymb; expIdx += (int)exRunLen) {
					exRunLen = isHuffman ? HuffmanTable.GetStdTbl(1).Decode(imgStrm)
										 : arithDecoder.DecodeInt(cxIAEX);
					if (exRunLen != 0)
						for (int index = expIdx; index < expIdx + exRunLen; index++)
							exportFlags[index] = curFlag;
					curFlag = 1 - curFlag;
				}
				expSymbols = new List<JB2Bmp>(amtExpSymb);
				for (int i = 0; i < amtImpSymb + amtNewSymb; i++)
					if (exportFlags[i] == 1)
						expSymbols.Add(i < amtImpSymb ? importSymbols[i] : newSymbols[i-amtImpSymb]);
			}
			long HuffDecodeBmSize() {
				bmSizeTable = bmSizeTable ?? segHdr.GetUserTable(sdHuffDecHeightSel, sdHuffDecWidthSel);
				return bmSizeTable.Decode(imgStrm);
			}
			int GetSbSymCodeLen() {
				var v = GetMaxBit(amtImpSymb + amtNewSymb);
				return isHuffman ? Math.Max(1, v) : v;
			}
			void SetSymbolsArray() {
				if (importSymbols == null)
					RetrieveImportSymbols();
				sbSymbols = sbSymbols ?? new List<JB2Bmp>(importSymbols);
			}
			void RetrieveImportSymbols() {
				importSymbols = new List<JB2Bmp>();
				foreach (SegmentHeader rs in segHdr.rtSegments)
					if (rs.segType == 0) {
						SymbolDictionary sd = (SymbolDictionary)rs.GetSegData();
						importSymbols.AddRange(sd.GetDictionary());
						amtImpSymb += sd.amtExpSymb;
					}
			}
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = sis.ReadInt(2);
				sdrTemplate			= (flags & 0x1000) != 0;	/* Bit 12 */
				sdTemplate			= (flags >> 10) & 3;		/* Bit 10-11 */
				isCodeCtxRetained	= (flags & 0x200) != 0;		/* Bit 9 */
				isCodeCtxUsed		= (flags & 0x100) != 0;     /* Bit 8 */
				sdHuffAggInstSel	= (flags >> 7) & 1;			/* Bit 7 */
				sdHuffBMSizeSel		= (flags >> 6) & 1;			/* Bit 6 */
				sdHuffDecWidthSel	= (flags >> 4) & 3;			/* Bit 4-5 */
				sdHuffDecHeightSel	= (flags >> 2) & 3;			/* Bit 2-3 */
				useRefineAggr		= (flags & 0x02) != 0;		/* Bit 1 */
				isHuffman			= (flags & 0x01) != 0;		/* Bit 0 */
				if (!isHuffman) {
					int noPix = sdTemplate == 0 ? 4 : 1;
					(sdATX, sdATY) = (new short[noPix], new short[noPix]);
					for (int i = 0; i < noPix; i++) 
						(sdATX[i], sdATY[i]) = (sis.ReadSByte(), sis.ReadSByte());
				}
				if (useRefineAggr && !sdrTemplate) {
					(sdrATX, sdrATY) = (new short[2], new short[2]);
					(sdrATX[0], sdrATY[0]) = (sis.ReadSByte(), sis.ReadSByte());
					(sdrATX[1], sdrATY[1]) = (sis.ReadSByte(), sis.ReadSByte());
				}
				amtExpSymb = sis.ReadInt(4);
				amtNewSymb = sis.ReadInt(4);
				if (segHdr.rtSegments != null)
					RetrieveImportSymbols();
				else
					importSymbols = new List<JB2Bmp>();
				if (isCodeCtxUsed) {
					SegmentHeader[] rtSegments = segHdr.rtSegments;
					for (int i = rtSegments.Length - 1; i >= 0; i--)
						if (rtSegments[i].segType == 0) {
							SymbolDictionary sd = (SymbolDictionary)rtSegments[i].GetSegData();
							if (sd.isCodeCtxRetained) {
								arithDecoder	= sd.arithDecoder;
								isHuffman		= sd.isHuffman;
								useRefineAggr	= sd.useRefineAggr;
								(sdTemplate, sdrTemplate) = (sd.sdTemplate, sd.sdrTemplate);
								(sdATX, sdrATX) = (sd.sdATX, sd.sdrATX);
								(sdATY, sdrATY) = (sd.sdATY, sd.sdrATY);
								cx = sd.cx;
							}
							break;
						}
				}
				if (isHuffman) {
					sdTemplate = 0;
					if (!useRefineAggr)
						isCodeCtxRetained = isCodeCtxUsed = false;
				}
				else
					sdHuffBMSizeSel = sdHuffDecWidthSel = sdHuffDecHeightSel = 0;
				if (!useRefineAggr)
					sdrTemplate = false;
				if (!isHuffman || !useRefineAggr)
					sdHuffAggInstSel = 0;
			}
		}
		internal class Region : SegmentData {
			internal RegSegInfo regInfo;    /** Region segment information field, 7.4.1 */
			internal JB2Bmp regBmp;        /** Decoded data as pixel values (use row stride/width to wrap line) */
			internal virtual JB2Bmp GetRegBmp() { return regBmp; }
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				regInfo = new RegSegInfo(imgStrm);
				regInfo.ParseHeader();
			}
		}
		internal class HalftoneRegion : Region {
			int hTemplate;
			bool hSkipEnabled, hDefPix, isMMR;
			int hGridWidth, hGridHeight;            /** Width of the gray-scale image, 7.4.5.1.2.1 */
			int hGridX, hGridY;                     /** Horizontal offset of the grid, 7.4.5.1.2.3 */
			int hRegX, hRegY;						/** Halftone grid vector, 7.4.5.1.3 */
			JB2Bmp halftoneRegBmp;					/** Decoded data */
			// Previously decoded data from other regions or dictionaries, stored to use as patterns in this region.
			List<JB2Bmp> patterns;
			// The procedure is described in JBIG2 ISO standard, 6.6.5.
			internal override JB2Bmp GetRegBmp() {
				if (null != halftoneRegBmp)
					return halftoneRegBmp;
				halftoneRegBmp = new JB2Bmp(regInfo.width, regInfo.height, hDefPix);
				if (patterns == null) {             /* 6.6.5, page 40 */
					patterns = new List<JB2Bmp>();
					foreach (SegmentHeader s in segHdr.rtSegments) 
						patterns.AddRange((s.GetSegData() as PatternDictionary).GetDictionary());
				}
				// 6.6.5.1 Computing hSkip - At the moment SKIP is not used... we are not able to test it.
				int bitsPerValue = GetMaxBit(patterns.Count()); /* 3) */
				int[][] grayScaleValues = GrayScaleDecoding(bitsPerValue);                      /* 4) */
				// 5), rendering the pattern, described in 6.6.5.2 
				for (int m = 0; m < hGridHeight; m++)											// 1)
					for (int n = 0; n < hGridWidth; n++) {										// a)
						int x = ShiftAndFill(hGridX + m * hRegY + n * hRegX) + hGridX, 
							y = ShiftAndFill(hGridY + m * hRegX - n * hRegY) + hGridY, 
							g = grayScaleValues[m][n];              // ii)
						patterns[g].blit(halftoneRegBmp, x, y, comboOper);
					}
				return halftoneRegBmp;                                                        /* 6) */
			}
			// Gray-scale image decoding procedure is special for halftone region decoding and is described in Annex C.5 on page 98.
			int[][] GrayScaleDecoding(int bitsPerValue) {
				short[] gbAtX = null, gbAtY = null;
				if (!isMMR) {
					short atx0 = (short)((hTemplate <= 1) ? 3 : (hTemplate >= 2 ? 2 : 0));
					gbAtX = new short[4] { atx0, -3, 2, -2 };
					gbAtY = new short[4] { -1, -1, -2, -2 };
				}
				JB2Bmp[] greyPlanes = new JB2Bmp[bitsPerValue];                // 1)
				GenRegion genReg = new GenRegion(imgStrm);
				genReg.SetParams(this, isMMR, hGridHeight, hGridWidth, hTemplate, gbAtX, gbAtY);
				int j = bitsPerValue - 1;                                           // 2)
				greyPlanes[j] = genReg.GetRegBmp();
				for (; j > 0;) {
					genReg.regBmp = null; ;
					greyPlanes[--j] = genReg.GetRegBmp();         // 3) a)
					for (int byteIdx = 0, y = 0; y < greyPlanes[j].height; y++)
						for (int x = 0; x < greyPlanes[j].width; x += 8) {
							byte newVal = greyPlanes[j + 1].bitmap[byteIdx];
							byte oldVal = greyPlanes[j].bitmap[byteIdx];
							greyPlanes[j].bitmap[byteIdx++]
								= JB2Bmp.CombineBytes(oldVal, newVal, ComboOper.XOR);
						}
				}
				// Gray-scale decoding procedure, page 98
				int[][] greyVals = new int[hGridHeight][];                   // 4)
				for (int y = 0; y < hGridHeight; y++) {
					greyVals[y] = new int[hGridWidth];
					for (int x = 0; x < hGridWidth; x += 8) {
						int minWidth = hGridWidth - x > 8 ? 8 : hGridWidth - x;
						int byteIdx = greyPlanes[0].GetIdx(x, y);
						for (int minX = 0; minX < minWidth; minX++) {
							int i = minX + x;
							greyVals[y][i] = 0;
							for (j = 0; j < bitsPerValue; j++)
								greyVals[y][i] += ((greyPlanes[j].bitmap[byteIdx] >> (7-i&7)) & 1) * (1 << j);
						}
					}
				}
				return greyVals;
			}
			int ShiftAndFill(int v) {
				v >>= 8;					// shift value by 8 and let the leftmost 8 bits be 0
				if (v >= 0) return v;
				int i = v | (v >> 1) | (v >> 2) | (v >> 4) | (v >> 8) | (v >> 16);
				int bp = GetMaxBit(i - (i >> 1));
				for (i = 1; i < 31 - bp; i++) // bit flip
					v |= 1 << (31 - i);
				return v;
			}
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadByte();
				hDefPix			= (flags & 0x80) != 0;					/* Bit 7 */
				comboOper		= (ComboOper)((flags >> 5) & 3);		/* Bit 4-6 */
				hSkipEnabled	= (flags & 0x08) != 0;					/* Bit 3 */
				hTemplate		= (flags >> 1) & 3;						/* Bit 1-2 */
				isMMR			= (flags & 0x01) != 0;					/* Bit 0 */
				hGridWidth		= imgStrm.ReadInt(4);
				hGridHeight		= imgStrm.ReadInt(4);
				hGridX			= imgStrm.ReadInt(4);
				hGridY			= imgStrm.ReadInt(4);
				hRegX			= imgStrm.ReadInt(2);
				hRegY			= imgStrm.ReadInt(2);
				dataLength		= imgStrm.Length - (dataOffset = imgStrm.Position);
			}
		}
		internal class GenRefineRegion : Region {
			internal class Template0 {
				internal int cxIdx;
				internal virtual int form(int c1, int c2, int c3, int c4, int c5) {
					return (c1 << 10) | (c2 << 7) | (c3 << 4) | (c4 << 1) | c5;
				}
			}
			internal class Template1 : Template0 {
				internal override int form(int c1, int c2, int c3, int c4, int c5) {
					return ((c1 & 0x02) << 8) | (c2 << 6) | ((c3 & 0x03) << 4) | (c4 << 1) | c5;
				}
			}
			static readonly Template0 T0 = new Template0 { cxIdx = 0x100 };
			static readonly Template1 T1 = new Template1 { cxIdx = 0x080 };
			/** Generic refinement region segment flags, 7.4.7.2 */
			bool			isTPGROn, fOverride, templateID;
			int				refDX, refDY;
			short[]			grAtX, grAtY;   
			bool[]			grAtOverride;
			JB2Bmp			refBmp;         
			ArithmDecoder	arithDecoder;
			CXStats cx;
			public GenRefineRegion() { }
			public GenRefineRegion(ImgStream subInputStream = null) {
				imgStrm = subInputStream;
				regInfo = new RegSegInfo(subInputStream);
			}
			internal override JB2Bmp GetRegBmp() {
				if (null != regBmp) return regBmp;
				int isLineTypicalPredicted = 0;     /* 6.3.5.6 - 1) */
				refBmp = refBmp ?? ((Region)segHdr.rtSegments[0].GetSegData()).GetRegBmp();
				arithDecoder = arithDecoder ?? new ArithmDecoder(imgStrm);
				cx = cx ?? new CXStats(8192);
				regBmp = new JB2Bmp(regInfo.width, regInfo.height); /* 6.3.5.6 - 2) */
				if (!templateID)                    // AT pixel may only occur in template 0
					UpdateOverride();
				int padWidth = (regBmp.width + 7) & -8;
				int dxStride = isTPGROn ? -refDY * refBmp.stride : 0;
				Template0 template = templateID ? T1 : T0;
				for (int y = 0; y < regBmp.height; y++) {   /* 6.3.5.6 - 3 */
					if (isTPGROn)                                       /* 6.3.5.6 - 3 b) */
						isLineTypicalPredicted ^= arithDecoder.DecodeBit(template.cxIdx, cx);
					if (isLineTypicalPredicted == 0)                    /* 6.3.5.6 - 3 c) */
						DecodeOptimized(y, regBmp.width, regBmp.stride, refBmp.stride);
					else                                                /* 6.3.5.6 - 3 d) */
						DecodeTypicalPredictedLine(y, regBmp.width, regBmp.stride, refBmp.stride, padWidth, dxStride);
				}
				return regBmp;                                      /* 6.3.5.6 - 4) */
			}
			void DecodeOptimized(int lnNum, int width, int stride, int refStride) {
				// Offset of the reference bitmap with respect to the bitmap being decoded
				// For example: if referenceDY = -1, y is 1 HIGHER that currY
				int curLine = lnNum - refDY;
				int refByteIdx = refBmp.GetIdx(Math.Max(0, -refDX), curLine);
				int byteIdx = regBmp.GetIdx(Math.Max(0, refDX), lnNum);
				Template0 template = templateID ? T1 : T0;
				int c1, c2, c3, c4, c5, w1 = 0, w2 = 0, w3 = 0, w4 = 0;
				if (curLine >= 1 && (curLine - 1) < refBmp.height)
					w1 = refBmp.GetInt(refByteIdx - refStride);
				if (curLine >= 0 && curLine < refBmp.height)
					w2 = refBmp.GetInt(refByteIdx);
				if (curLine >= -1 && curLine + 1 < refBmp.height)
					w3 = refBmp.GetInt(refByteIdx + refStride);
				refByteIdx++;
				if (lnNum >= 1)
					w4 = regBmp.GetInt(byteIdx - stride);
				byteIdx++;
				int modReferenceDX = refDX % 8, shiftOffset = 6 + modReferenceDX,
					modRefByteIdx = refByteIdx % refStride;
				if (shiftOffset >= 0) {
					c1 = (shiftOffset >= 8 ? 0 : (int)((uint)w1 >> shiftOffset)) & 0x07;
					c2 = (shiftOffset >= 8 ? 0 : (int)((uint)w2 >> shiftOffset)) & 0x07;
					c3 = (shiftOffset >= 8 ? 0 : (int)((uint)w3 >> shiftOffset)) & 0x07;
					if (shiftOffset == 6 && modRefByteIdx > 1) {
						if (curLine >= 1 && (curLine - 1) < refBmp.height)
							c1 |= refBmp.GetInt(refByteIdx - refStride - 2) << 2 & 0x04;
						if (curLine >= 0 && curLine < refBmp.height)
							c2 |= refBmp.GetInt(refByteIdx - 2) << 2 & 0x04;
						if (curLine >= -1 && curLine + 1 < refBmp.height)
							c3 |= refBmp.GetInt(refByteIdx + refStride - 2) << 2 & 0x04;
					}
					if (shiftOffset == 0) {
						w1 = w2 = w3 = 0;
						if (modRefByteIdx < refStride - 1) {
							if (curLine >= 1 && (curLine - 1) < refBmp.height)
								w1 = refBmp.GetInt(refByteIdx - refStride);
							if (curLine >= 0 && curLine < refBmp.height)
								w2 = refBmp.GetInt(refByteIdx);
							if (curLine >= -1 && curLine + 1 < refBmp.height)
								w3 = refBmp.GetInt(refByteIdx + refStride);
						}
						refByteIdx++;
					}
				}
				else {
					c1 = (w1 << 1) & 0x07;
					c2 = (w2 << 1) & 0x07;
					c3 = (w3 << 1) & 0x07;
					w1 = w2 = w3 = 0;
					if (modRefByteIdx < refStride - 1) {
						if (curLine >= 1 && (curLine - 1) < refBmp.height)
							w1 = refBmp.GetInt(refByteIdx - refStride);
						if (curLine >= 0 && curLine < refBmp.height)
							w2 = refBmp.GetInt(refByteIdx);
						if (curLine >= -1 && curLine + 1 < refBmp.height)
							w3 = refBmp.GetInt(refByteIdx + refStride);
						refByteIdx++;
					}
					c1 |= (int)((uint)w1 >> 7) & 0x07;
					c2 |= (int)((uint)w2 >> 7) & 0x07;
					c3 |= (int)((uint)w3 >> 7) & 0x07;
				}
				c4 = (int)((uint)w4 >> 6);
				c5 = 0;
				int modBitsToTrim = (2 - modReferenceDX) % 8;
				w1 <<= modBitsToTrim;
				w2 <<= modBitsToTrim;
				w3 <<= modBitsToTrim;
				w4 <<= 2;
				for (int x = 0; x < width; x++) {
					int minX = x & 0x07;
					int tval = template.form(c1, c2, c3, c4, c5);
					int bit = arithDecoder.DecodeBit(!fOverride ? tval
						: OverrideAtTemplate0(tval, x, lnNum, regBmp.bitmap[regBmp.GetIdx(x,lnNum)], minX), cx);
					regBmp.SetPixel(x, lnNum, (byte)bit);
					c1 = ((c1 << 1) | 0x01 & (int)((uint)w1 >> 7)) & 0x07;
					c2 = ((c2 << 1) | 0x01 & (int)((uint)w2 >> 7)) & 0x07;
					c3 = ((c3 << 1) | 0x01 & (int)((uint)w3 >> 7)) & 0x07;
					c4 = ((c4 << 1) | 0x01 & (int)((uint)w4 >> 7)) & 0x07;
					c5 = bit;
					if ((x - refDX) % 8 == 5) {
						if (((x - refDX) / 8) + 1 >= refBmp.stride)
							w1 = w2 = w3 = 0;
						else {
							w1 = (curLine >= 1 && (curLine - 1 < refBmp.height))
								? refBmp.GetInt(refByteIdx - refStride) : 0;
							w2 = (curLine >= 0 && curLine < refBmp.height)
								? refBmp.GetInt(refByteIdx) : 0;
							w3 = (curLine >= -1 && (curLine + 1) < refBmp.height)
								? refBmp.GetInt(refByteIdx + refStride) : 0;
						}
						refByteIdx++;
					}
					else {
						w1 <<= 1;
						w2 <<= 1;
						w3 <<= 1;
					}
					if (minX == 5 && lnNum >= 1) {
						w4 = ((x >> 3) + 1 >= regBmp.stride) ? 0 : regBmp.GetInt(byteIdx - stride);
						byteIdx++;
					}
					else
						w4 <<= 1;
				}
			}
			void UpdateOverride() {
				if (grAtX == null || grAtY == null || grAtX.Length != grAtY.Length)
					return;
				grAtOverride = new bool[grAtX.Length];
				if (templateID)
					fOverride = false;
				else {
					if (grAtX[0] != -1 && grAtY[0] != -1)
						grAtOverride[0] = fOverride = true;
					if (grAtX[1] != -1 && grAtY[1] != -1)
						grAtOverride[1] = fOverride = true;
				}
			}
			void DecodeTypicalPredictedLine(int lnNum, int width,
					int stride, int refStride, int pad, int dltRefStride) {
				int curLn = lnNum - refDY;                  // Offset of the reference bitmap with respect 
				int refByteIdx = refBmp.GetIdx(0, curLn);   // to the bitmap being decoded. For example: 
				int byteIdx = regBmp.GetIdx(0, lnNum);      // if grReferenceDY = -1, y is 1 HIGHER that currY
				if (templateID)
					DecodeTypicalPredictedLineTemplate1(lnNum, width, stride, refStride,
									pad, dltRefStride, byteIdx, curLn, refByteIdx);
				else
					DecodeTypicalPredictedLineTemplate0(lnNum, width, stride, refStride,
									pad, dltRefStride, byteIdx, curLn, refByteIdx);
			}
			void DecodeTypicalPredictedLineTemplate0(int lnNum, int width, int stride, 
					int refStride, int pad, int dltRefStride, int byteIdx, int lnCur, int refByteIdx) {
				int prevLn = (lnNum > 0) ? regBmp.GetInt(byteIdx - stride) : 0;
				int prvRefLn = (lnCur > 0 && lnCur <= refBmp.height)
					? refBmp.GetInt(refByteIdx - refStride + dltRefStride) << 4 : 0;
				int curRefLn = (lnCur >= 0 && lnCur < refBmp.height)
					? refBmp.GetInt(refByteIdx + dltRefStride) << 1 : 0;
				int nxtRefLn = (lnCur > -2 && lnCur < (refBmp.height - 1))
					? refBmp.GetInt(refByteIdx + refStride + dltRefStride) : 0;
				int ctx = ((prevLn >> 5) & 0x6) | ((nxtRefLn >> 2) & 0x30)
						| (curRefLn & 0x180) | (prvRefLn & 0xc00);
				int cxCtx, nextByte;
				for (int x = 0; x < pad; x = nextByte, refByteIdx++) {
					nextByte = x + 8;
					int res = 0, minWidth = width - x > 8 ? 8 : width - x, 
						yOff = dltRefStride + 1;
					bool readNextByte = nextByte < width, refReadNextByte = nextByte < refBmp.width;
					if (lnNum > 0)
						prevLn = (prevLn << 8)
							| (readNextByte ? regBmp.GetInt(byteIdx - stride + 1) : 0);
					if (lnCur > 0 && lnCur <= refBmp.height)
						prvRefLn = (prvRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx - refStride + yOff) << 4 : 0);
					if (lnCur >= 0 && lnCur < refBmp.height)
						curRefLn = (curRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx + yOff) << 1 : 0);
					if (lnCur > -2 && lnCur < (refBmp.height - 1))
						nxtRefLn = (nxtRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx + refStride + yOff) : 0);
					for (int minX = 0; minX < minWidth; minX++) {
						int bmpVal = (ctx >> 4) & 0x1FF, toShift = 7 - minX,
							bit = (bmpVal == 0x1ff) ? 1 : 0;
						if (bmpVal != 0x1ff && bmpVal != 0x00) {
							cxCtx = !fOverride ? ctx : OverrideAtTemplate0(ctx, x + minX, lnNum, res, minX);
							bit = arithDecoder.DecodeBit(cxCtx, cx);
						}
						res |= bit << toShift;
						ctx = ((ctx & 0xdb6) << 1) | bit | ((prevLn >> toShift + 5) & 0x002)
								| ((nxtRefLn >> toShift + 2) & 0x010)
								| ((curRefLn >> toShift) & 0x080)
								| ((prvRefLn >> toShift) & 0x400);
					}
					regBmp.bitmap[byteIdx++] = (byte)res;
				}
			}
			void DecodeTypicalPredictedLineTemplate1(int lnNum, int width, int stride,
					int refStride, int pad, int dltRefStride, int byteIdx, int lnCur, int refByteIdx) {
				int nextByte, prevLn = (lnNum > 0) ? regBmp.GetInt(byteIdx - stride) : 0;
				int prvRefLn = (lnCur > 0 && lnCur <= refBmp.height)
					? refBmp.GetInt(byteIdx - refStride + dltRefStride) << 2 : 0;
				int curRefLn = (lnCur >= 0 && lnCur < refBmp.height)
					? refBmp.GetInt(byteIdx + dltRefStride) : 0;
				int nxtRefLn = (lnCur > -2 && lnCur < (refBmp.height - 1))
					? refBmp.GetInt(byteIdx + refStride + dltRefStride) : 0;
				int ctx = ((prevLn >> 5) & 0x6) | ((nxtRefLn >> 2) & 0x30)
							| (curRefLn & 0xc0) | (prvRefLn & 0x200);
				int grRefVal = ((nxtRefLn >> 2) & 0x70) | (curRefLn & 0xc0)
								| (prvRefLn & 0x700);
				for (int x = 0; x < pad; x = nextByte, refByteIdx++) {
					nextByte = x + 8;
					int res = 0, minWidth = width - x > 8 ? 8 : width - x, yOff = dltRefStride + 1;
					bool readNextByte = nextByte < width, refReadNextByte = nextByte < refBmp.width;
					if (lnNum > 0)
						prevLn = (prevLn << 8)
							| (readNextByte ? regBmp.GetInt(byteIdx - stride + 1) : 0);
					if (lnCur > 0 && lnCur <= refBmp.height)
						prvRefLn = (prvRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx - refStride + yOff) << 2 : 0);
					if (lnCur >= 0 && lnCur < refBmp.height)
						curRefLn = (curRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx + yOff) : 0);
					if (lnCur > -2 && lnCur < (refBmp.height - 1))
						nxtRefLn = (nxtRefLn << 8)
							| (refReadNextByte ? refBmp.GetInt(refByteIdx + refStride + yOff) : 0);
					for (int minorX = 0; minorX < minWidth; minorX++) {
						int bmpVal = (grRefVal >> 4) & 0x1ff;       // i)
						int bit = (bmpVal == 0x1ff) ? 1
							: (bmpVal == 0x00 ? 0 : arithDecoder.DecodeBit(ctx, cx));
						int toShift = 7 - minorX;
						res |= bit << toShift;
						ctx = ((ctx & 0x0d6) << 1) | bit | ((prevLn >> toShift + 5) & 0x002)
								| ((nxtRefLn >> toShift + 2) & 0x010) | ((curRefLn >> toShift) & 0x040)
								| ((prvRefLn >> toShift) & 0x200);
						grRefVal = ((grRefVal & 0x0db) << 1) | ((nxtRefLn >> toShift + 2) & 0x010)
								| ((curRefLn >> toShift) & 0x080) | ((prvRefLn >> toShift) & 0x400);
					}
					regBmp.bitmap[byteIdx++] = (byte)res;
				}
			}
			int OverrideAtTemplate0(int ctx, int x, int y, int result, int minX) {
				if (grAtOverride[0])
					ctx = (ctx & 0xfff7) | (grAtY[0] == 0 && grAtX[0] >= -minX
							? (result >> (7 - (minX + grAtX[0])) & 0x1) << 3
							: regBmp.GetPixel(x + grAtX[0], y + grAtY[0]) << 3);
				if (grAtOverride[1])
					ctx = (ctx & 0xefff) | (grAtY[1] == 0 && grAtX[1] >= -minX
						? (result >> (7 - (minX + grAtX[1])) & 0x1) << 12
						: refBmp.GetPixel(x + grAtX[1] + refDX, y + grAtY[1] + refDY) << 12);
				return ctx;
			}
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadByte();
				isTPGROn = (flags & 2) != 0;				/* Bit 1 */
				templateID = (flags & 1) != 0;				/* Bit 0 */
				if (!templateID) {
					(grAtX, grAtY) = (new short[2], new short[2]);
					(grAtX[0], grAtY[0]) = (imgStrm.ReadSByte(), imgStrm.ReadSByte());
					(grAtX[1], grAtY[1]) = (imgStrm.ReadSByte(), imgStrm.ReadSByte());
				}
			}
			internal void SetParams(CXStats cx, ArithmDecoder arithDecoder, 
					bool templt, int regWidth, int regHeight, JB2Bmp grRef, 
					int refDX, int refDY, short[] grAtX, short[] grAtY) {
				this.cx = cx ?? this.cx;
				this.arithDecoder = arithDecoder ?? this.arithDecoder;
				(templateID, refBmp, regBmp, isTPGROn) = (templt, grRef, null, false);
				(this.refDX, this.grAtX) = (refDX, grAtX);
				(this.refDY, this.grAtY) = (refDY, grAtY);
				(regInfo.width, regInfo.height) = (regWidth, regHeight);
			}
		}
		internal class TextRegion : Region {
			bool			useRefinement, isHuffman, sbrTemplate, defPix;
			int				sbdsOffset, isTransposed, refCorner, logSBStrips, sbStrips, 
							amtOfSymb, symbCodeLen, sbHuffRSize, sbHuffRDY, sbHuffRDX, 
							sbHuffRDHeight, sbHuffRDWidth, sbHuffDT, sbHuffDS, sbHuffFS;
			short[]			sbrATX, sbrATY;             /** Text region refinement AT flags, 7.4.3.1.3 */
			long			amtOfSymbInst, currentS;    /** Further parameters */
			List<JB2Bmp>	symbols = new List<JB2Bmp>();
			ArithmDecoder	arithDecoder;
			GenRefineRegion genRefineReg;
			CXStats			cxIADT, cxIAFS, cxIADS, cxIAIT, cxIARI, cxIARDW, 
							cxIARDH, cxIAID, cxIARDX, cxIARDY, cx;
			HuffmanTable	symbCodeTbl, fsTable, dsTable, table, rdwTable, 
							rdhTable, rdxTable, rdyTable, rSizeTable;
			internal override JB2Bmp GetRegBmp() {
				if (!isHuffman) {
					cxIADT	= cxIADT	?? new CXStats();
					cxIAFS	= cxIAFS	?? new CXStats();
					cxIADS	= cxIADS	?? new CXStats();
					cxIAIT	= cxIAIT	?? new CXStats();
					cxIARI	= cxIARI	?? new CXStats();
					cxIARDW = cxIARDW	?? new CXStats();
					cxIARDH = cxIARDH	?? new CXStats();
					cxIAID	= cxIAID	?? new CXStats(1 << symbCodeLen);
					cxIARDX	= cxIARDX	?? new CXStats();
					cxIARDY	= cxIARDY	?? new CXStats();
					arithDecoder = arithDecoder ?? new ArithmDecoder(imgStrm);
				}
				regBmp = new JB2Bmp(regInfo.width, regInfo.height, defPix);
				DecodeSymbolInstances();
				return regBmp;						/* 4) */
			}
			void DecodeSymbolInstances() {
				long stripT = DecodeStripT(-sbStrips);				/* Last two sentences of 6.4.5 2) */
				for (long c = 0, s1 = 0; c < amtOfSymbInst; ) {		/* 6.4.5 3 a) */
					stripT += DecodeStripT(sbStrips);
					bool first = true;                              /* 3 c) symbol instances in the strip */
					for (currentS = 0; ; c++) {						// do until OOB
						if (first) {								/* 3 c) i) - first symbol instance in the strip */
							currentS = s1 += DecodeDfS();			/* 6.4.7 */
							first = false;
						}											/* 3 c) ii) - the remaining symbol instances in the strip */
						else {
							long idS = DecodeIdS();                 /* 6.4.8 */
							if (idS == long.MaxValue || c >= amtOfSymbInst)
								break;
							currentS += (idS + sbdsOffset);
						}
						long t  = stripT + DecodeCurrentT();        /* 3 c) iii) */
						long id = DecodeID();                       /* 3 c) iv) */
						long r  = DecodeRI();                       /* 3 c) v) */
						JB2Bmp ib = DecodeIb(r, id);                /* 6.4.11 */
						blit(ib, t);                                /* vi) */
					}
				}
			}
			long DecodeStripT(int s) {
				if (!isHuffman)
					return s * arithDecoder.DecodeInt(cxIADT);
				if (sbHuffDT != 3)
					return s * HuffmanTable.GetStdTbl(11 + sbHuffDT).Decode(imgStrm);
				table = table ?? segHdr.GetUserTable(sbHuffFS, sbHuffDS);
				return s * table.Decode(imgStrm);
			}
			long DecodeDfS() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIAFS);
				if (sbHuffFS != 3)
					return HuffmanTable.GetStdTbl(6 + sbHuffFS).Decode(imgStrm);
				return (fsTable = fsTable ?? segHdr.GetUserTable()).Decode(imgStrm);
			}
			long DecodeIdS() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIADS);
				if (sbHuffDS != 3)
					return HuffmanTable.GetStdTbl(8 + sbHuffDS).Decode(imgStrm);
				return (dsTable = dsTable ?? segHdr.GetUserTable(sbHuffFS)).Decode(imgStrm);
			}
			long DecodeCurrentT() {
				if (sbStrips == 1) return 0;
				return isHuffman ? imgStrm.ReadBits(logSBStrips) : arithDecoder.DecodeInt(cxIAIT);
			}
			long DecodeID() {
				if (!isHuffman)
					return arithDecoder.DecodeIAID(symbCodeLen, cxIAID);
				return (symbCodeTbl == null) 
					? imgStrm.ReadBits(symbCodeLen) : symbCodeTbl.Decode(imgStrm);
			}
			long DecodeRI() {
				if (!useRefinement) return 0;
				return isHuffman ? imgStrm.ReadBits(1) : arithDecoder.DecodeInt(cxIARI);
			}
			JB2Bmp DecodeIb(long r, long id) {
				JB2Bmp ib;
				if (r == 0)
					ib = symbols[(int)id];
				else {										/* 1) - 4) */
					long rdw = DecodeRdw(), rdh = DecodeRdh(), rdx = DecodeRdx(), rdy = DecodeRdy();
					if (isHuffman) {						/* 5) */
						DecodeSymInRefSize();
						imgStrm.Seek(0, SeekOrigin.Current);// clear bitBuffer
					}
					JB2Bmp ibo = symbols[(int)id];			/* 6) */
					genRefineReg = genRefineReg ?? new GenRefineRegion(imgStrm);
					genRefineReg.SetParams(cx, arithDecoder, sbrTemplate,
							(int)(ibo.width + rdw), (int)(ibo.height + rdh), ibo,
							(int)((rdw >> 1) + rdx), (int)((rdh >> 1) + rdy), sbrATX, sbrATY);
					ib = genRefineReg.GetRegBmp();
					if (isHuffman)							/* 7 */
						imgStrm.Seek(0, SeekOrigin.Current);// clear bitBuffer
				}
				return ib;
			}
			long DecodeRdw() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIARDW);
				if (sbHuffRDWidth != 3)
					return HuffmanTable.GetStdTbl(14 + sbHuffRDWidth).Decode(imgStrm);
				rdwTable = rdwTable ?? segHdr.GetUserTable(sbHuffFS, sbHuffDS, sbHuffDT);
				return rdwTable.Decode(imgStrm);
			}
			long DecodeRdh() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIARDH);
				if (sbHuffRDHeight != 3)
					return HuffmanTable.GetStdTbl(14 + sbHuffRDHeight).Decode(imgStrm);
				rdhTable = rdhTable ?? segHdr.GetUserTable(sbHuffFS, sbHuffDS, sbHuffDT, sbHuffRDWidth);
				return rdhTable.Decode(imgStrm);
			}
			long DecodeRdx() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIARDX);
				if (sbHuffRDX != 3)
					return HuffmanTable.GetStdTbl(14 + sbHuffRDX).Decode(imgStrm);
				rdxTable = rdxTable 
					?? segHdr.GetUserTable(sbHuffFS, sbHuffDS, sbHuffDT, sbHuffRDWidth, sbHuffRDHeight);
				return rdxTable.Decode(imgStrm);
			}
			long DecodeRdy() {
				if (!isHuffman)
					return arithDecoder.DecodeInt(cxIARDY);
				if (sbHuffRDY != 3)
					return HuffmanTable.GetStdTbl(14 + sbHuffRDY).Decode(imgStrm);
				rdyTable = rdyTable 
					?? segHdr.GetUserTable(sbHuffFS, sbHuffDS, sbHuffDT, sbHuffRDWidth, sbHuffRDHeight, sbHuffRDX);
				return rdyTable.Decode(imgStrm);
			}
			long DecodeSymInRefSize() {
				if (sbHuffRSize == 0)
					return HuffmanTable.GetStdTbl(1).Decode(imgStrm);
				rSizeTable = rSizeTable 
					?? segHdr.GetUserTable(sbHuffFS, sbHuffDS, sbHuffDT, sbHuffRDWidth, sbHuffRDHeight, sbHuffRDX, sbHuffRDY);
				return rSizeTable.Decode(imgStrm);
			}
			void blit(JB2Bmp ib, long t) {
				if (isTransposed == 0 && (refCorner == 2 || refCorner == 3))
					currentS += ib.width - 1;
				else if (isTransposed == 1 && (refCorner == 0 || refCorner == 2))
					currentS += ib.height - 1;
				long s = currentS;                      /* vii) */
				if (isTransposed == 1)					/* viii) */
					(t, s) = (s, t);					// swap
				if (refCorner != 1) {
					if (refCorner == 0)					// BL
						t -= ib.height - 1;
					else if (refCorner == 2) {			// BR
						t -= ib.height - 1;
						s -= ib.width - 1;
					}
					else if (refCorner == 3)			// TR
						s -= ib.width - 1;
				}
				ib.blit(regBmp, (int)s, (int)t, comboOper); /* x) */
				if (isTransposed == 0 && (refCorner == 0 || refCorner == 1))
					currentS += ib.width - 1;
				if (isTransposed == 1 && (refCorner == 1 || refCorner == 3))
					currentS += ib.height - 1;
			}
			void InitSymbols() {
				foreach (SegmentHeader segment in segHdr.rtSegments)
					if (segment.segType == 0) {
						SymbolDictionary sd = (SymbolDictionary)segment.GetSegData();
						sd.cxIAID = cxIAID;
						symbols.AddRange(sd.GetDictionary());
					}
				amtOfSymb = symbols.Count();
			}
			void SymbolIDCodeLengths() {
				var runCodeTable = new List<HuffmanTable.Code>();         /* 1) - 2) */
				for (int i = 0; i < 35; i++) {
					int prefLen = imgStrm.ReadBits(4) & 0xf;
					if (prefLen > 0)
						runCodeTable.Add(new HuffmanTable.Code(prefLen, 0, i, false));
				}
				HuffmanTable ht = new HuffmanTable(runCodeTable);
				long prevLen = 0;                        /* 3) - 5) */
				var sbSymCodes = new List<HuffmanTable.Code>();
				for (int cnt = 0; cnt < amtOfSymb;) {
					long code = ht.Decode(imgStrm);
					if (code < 32) {
						if (code > 0)
							sbSymCodes.Add(new HuffmanTable.Code((int)code, 0, cnt, false));
						prevLen = code;
						cnt++;
					}
					else {
						long runLen = 0, curLen = 0;
						if (code == 32) {
							runLen = 3 + imgStrm.ReadBits(2);
							if (cnt > 0)
								curLen = prevLen;
						}
						else if (code == 33)
							runLen = 3 + imgStrm.ReadBits(3);
						else if (code == 34)
							runLen = 11 + imgStrm.ReadBits(7);
						for (int j = 0; j < runLen; j++, cnt++)
							if (curLen > 0)
								sbSymCodes.Add(new HuffmanTable.Code((int)curLen, 0, cnt, false));
					}
				}
				imgStrm.Seek(0, SeekOrigin.Current); // clear bitBuffer
				symbCodeTbl = new HuffmanTable(sbSymCodes); /* 7) */
			}
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadInt(2);
				sbrTemplate		= (flags & 0x8000) != 0;			/* Bit 15 */
				sbdsOffset		= (flags >> 10) & 0x1f;				/* Bit 10-14 */
				if (sbdsOffset > 0x0f) sbdsOffset -= 0x20;
				defPix			= (flags & 0x200) != 0;				/* Bit 9 */
				comboOper		= (ComboOper)((flags >> 7) & 0x3);	/* Bit 7-8 */
				isTransposed	= (flags >> 6) & 0x01;				/* Bit 6 */
				refCorner		= (flags >> 4) & 0x3;				/* Bit 4-5 */
				logSBStrips		= (flags >> 2) & 0x3;				/* Bit 2-3 */
				sbStrips		= 1 << logSBStrips;
				useRefinement	= (flags & 0x02) != 0;				/* Bit 1 */
				isHuffman		= (flags & 0x01) != 0;				/* Bit 0 */
				if (isHuffman) {
					flags = imgStrm.ReadInt(2);
					sbHuffRSize		= (flags >> 14) & 1;            /* Bit 14 */
					sbHuffRDY		= (flags >> 12) & 3;			/* Bit 12-13 */
					sbHuffRDX		= (flags >> 10) & 3;			/* Bit 10-11 */
					sbHuffRDHeight	= (flags >>  8) & 3;			/* Bit 8-9 */
					sbHuffRDWidth	= (flags >>  6) & 3;			/* Bit 6-7 */
					sbHuffDT		= (flags >>  4) & 3;			/* Bit 4-5 */
					sbHuffDS		= (flags >>  2) & 3;			/* Bit 2-3 */
					sbHuffFS		= flags & 3;					/* Bit 0-1 */
				}
				if (useRefinement && !sbrTemplate) {
					(sbrATX, sbrATY) = (new short[2], new short[2]);
					(sbrATX[0], sbrATY[0]) = (imgStrm.ReadSByte(), imgStrm.ReadSByte());
					(sbrATX[1], sbrATY[1]) = (imgStrm.ReadSByte(), imgStrm.ReadSByte());
				}
				amtOfSymbInst = imgStrm.ReadInt(4);
				long pixels = regInfo.width * regInfo.height;
				if (pixels < amtOfSymbInst)				// don't decode more than 1 symbol/pixel
					amtOfSymbInst = pixels;
				if (segHdr.rtSegments != null)
					InitSymbols();									/* 7.4.3.1.7 */
				if (isHuffman)
					SymbolIDCodeLengths();
				else
					symbCodeLen = GetMaxBit(amtOfSymb);
				if (!useRefinement)
					sbrTemplate = false;
				if (sbHuffFS==2 || sbHuffRDWidth==2 || sbHuffRDHeight==2 || sbHuffRDX==2 || sbHuffRDY==2)
					throw new Exception("Huffman flag value of text region segment is not permitted");
				if (!useRefinement)
					sbHuffRSize = sbHuffRDY = sbHuffRDX = sbHuffRDWidth = sbHuffRDHeight = 0;
			}
			internal void SetContexts(CXStats cx, CXStats dt, CXStats fs, CXStats ds,
					CXStats it, CXStats id, CXStats rdw, CXStats rdh, CXStats rdx, CXStats rdy) {
				(this.cx, cxIADT, cxIAFS, cxIADS, cxIAIT, cxIAID, cxIARDW, cxIARDH, cxIARDX, cxIARDY)
					= (cx, dt, fs, ds, it, id, rdw, rdh, rdx, rdy);
			}
			internal void SetParams(ArithmDecoder arithDecoder, bool isHuffman, int sbw,
					int sbh, long sbNumInstances, int sbNumSyms, bool sbrTemplate, 
					short[] sbrATX, short[] sbrATY, List<JB2Bmp> sbSyms, int sbSymCodeLen) {
				this.arithDecoder = arithDecoder;
				this.isHuffman = isHuffman;
				this.regInfo.width = sbw;
				this.regInfo.height = sbh;
				this.amtOfSymbInst = sbNumInstances;
				this.amtOfSymb = sbNumSyms;
				this.sbrTemplate = sbrTemplate;
				this.sbrATX = sbrATX;
				this.sbrATY = sbrATY;
				this.symbols = sbSyms;
				this.symbCodeLen = sbSymCodeLen;
				this.useRefinement = true;
				this.comboOper = 0;
				this.refCorner = this.sbStrips = 1;
				this.defPix = false;
				isTransposed = sbdsOffset = sbHuffFS = sbHuffDS = sbHuffDT 
					= sbHuffRDWidth = sbHuffRDHeight = sbHuffRDX = sbHuffRDY = sbHuffRSize = 0;
			}
		}
		internal class GenRegion : Region {
			bool	useExtTemplates, isTPGDon;			
			int		gbTemplate;
			short[] gbAtX, gbAtY;				/** Generic region segment AT flags, 7.4.6.3 */
			bool[]	gbAtOverride;
			bool	fOverride, isMMR;                      
			ArithmDecoder arithDecoder;
			CXStats cx;
			MMRDecompressor mmrDecompressor;
			public GenRegion() {
			}
			public GenRegion(ImgStream sis) {
				this.imgStrm = sis;
				this.regInfo = new RegSegInfo(sis);
			}
			internal override JB2Bmp GetRegBmp() {
				if (null != regBmp) return regBmp;
				if (isMMR) {
					if (null == mmrDecompressor)                    // MMR DECODER CALL
						mmrDecompressor = new MMRDecompressor(regInfo.width, regInfo.height, imgStrm, dataLength);
					regBmp = mmrDecompressor.Uncompress();  /* 6.2.6 */
				}
				else {
					UpdateOverrideFlags();                          // ARITHMETIC DECODER PROCEDURE for generic region segments
					arithDecoder = arithDecoder ?? new ArithmDecoder(imgStrm);
					cx = cx ?? new CXStats(65536);                      /* 6.2.5.7 - 2) */
					regBmp = new JB2Bmp(regInfo.width, regInfo.height);
					int pad = (regBmp.width + 7) & -8;
					for (int ltp = 0, line = 0; line < regBmp.height; line++) {
						int dst = line * regBmp.stride, src = dst - regBmp.stride;
						if (isTPGDon)                               /* 6.2.5.7 - 3 b) */
							ltp ^= DecodeSLTP();
						if (ltp != 1)                               /* 6.2.5.7 - 3 c) */
							switch (gbTemplate) {
								case 0:
									if (!useExtTemplates)
										DecodeTemplate0a(line, pad, dst, src, regBmp.width, regBmp.stride);
									else
										DecodeTemplate0b(line, pad, dst, src, regBmp.width, regBmp.stride);
									break;
								case 1: DecodeTemplate1(line, pad, dst, src, regBmp.width, regBmp.stride); break;
								case 2: DecodeTemplate2(line, pad, dst, src, regBmp.width, regBmp.stride); break;
								case 3: DecodeTemplate3(line, pad, dst, src); break;
							}
						else if (line > 0)
							Array.Copy(regBmp.bitmap, src, regBmp.bitmap, dst, regBmp.stride);
					}
				}
				return regBmp;
			}
			int DecodeSLTP() {
				int ctx = 0;
				switch (gbTemplate) {
					case 0: ctx = 0x9b25;	break;
					case 1: ctx = 0x0795;	break;
					case 2: ctx = 0x00e5;	break;
					case 3: ctx = 0x0195;	break;
				}
				return arithDecoder.DecodeBit(ctx, cx);
			}
			void DecodeTemplate0a(int line, int pad, int dst, int src, int width, int stride) {
				int line1 = (line >= 1) ? regBmp.GetInt(src) : 0, 
					line2 = (line >= 2) ? regBmp.GetInt(src - stride) << 6 : 0, 
					ctx = (line1 & 0xf0) | (line2 & 0x3800), nxtByte;
				for (int x = 0; x < pad; x = nxtByte, src++) {
					int minWidth = width - x > 8 ? 8 : width - x, res = 0;
					nxtByte = x + 8;
					if (line > 0)
						line1 = (line1 << 8)
							| (nxtByte < width ? regBmp.GetInt(src + 1) : 0);
					if (line > 1)
						line2 = (line2 << 8)
							| (nxtByte < width ? regBmp.GetInt(src - stride + 1) << 6 : 0);
					for (int minX = 0; minX < minWidth; minX++) {
						int toShift = 7 - minX, 
							cxIdx = !fOverride ? ctx
								: OverrideAtTemplate0a(ctx, (x + minX), line, res, minX, toShift),
							bit = arithDecoder.DecodeBit(cxIdx, cx);
						res |= bit << toShift;
						ctx = ((ctx & 0x7bf7) << 1) | bit | ((line1 >> toShift) & 0x10)
								| ((line2 >> toShift) & 0x800);
					}
					regBmp.bitmap[dst++] = (byte)res;
				}
			}
			void DecodeTemplate0b(int line, int pad, int dst, int src, int width, int stride) {
				int ln1 = (line >= 1) ? regBmp.GetInt(src) : 0,
					ln2 = (line >= 2) ? regBmp.GetInt(src - stride) << 6 : 0,
					ctx = (ln1 & 0xf0) | (ln2 & 0x3800), nxtByte;
				for (int x = 0; x < pad; x = nxtByte, src++) {
					int minWidth = width - x > 8 ? 8 : width - x, res = 0;  /* 6.2.5.7 3d */
					nxtByte = x + 8;
					if (line > 0)
						ln1 = (ln1 << 8)
							| (nxtByte < width ? regBmp.GetInt(src + 1) : 0);
					if (line > 1)
						ln2 = (ln2 << 8)
							| (nxtByte < width ? regBmp.GetInt(src - stride + 1) << 6 : 0);
					for (int minX = 0; minX < minWidth; minX++) {
						int toShift = 7 - minX,
							cxIdx = (!fOverride) ? ctx
								: OverrideAtTemplate0b(ctx, (x + minX), line, res, minX, toShift),
							bit = arithDecoder.DecodeBit(cxIdx, cx);
						res |= bit << toShift;
						ctx = ((ctx & 0x7bf7) << 1) | bit | ((ln1 >> toShift) & 0x10)
								| ((ln2 >> toShift) & 0x800);
					}
					regBmp.bitmap[dst++] = (byte)res;
				}
			}
			void DecodeTemplate1(int line, int pad, int dst, int src, int width, int stride) {
				int ln1 = (line >= 1) ? regBmp.GetInt(src) : 0,
					ln2 = (line >= 2) ? regBmp.GetInt(src - stride) << 5 : 0, 
					ctx = ((ln1 >> 1) & 0x1f8) | ((ln2 >> 1) & 0x1e00), nxtByte;
				for (int x = 0; x < pad; x = nxtByte, src++) {
					nxtByte = x + 8;
					int minWidth = width - x > 8 ? 8 : width - x, res = 0;	/* 6.2.5.7 3d */
					if (line >= 1)
						ln1 = (ln1 << 8)
							| (nxtByte < width ? regBmp.GetInt(src + 1) : 0);
					if (line >= 2)
						ln2 = (ln2 << 8)
							| (nxtByte < width ? regBmp.GetInt(src - stride + 1) << 5 : 0);
					for (int minX = 0; minX < minWidth; minX++) {
						int cxCtx = !fOverride ? ctx
								: OverrideAtTemplate1(ctx, x + minX, line, res, minX),
							bit = arithDecoder.DecodeBit(cxCtx, cx), toShift = 8 - minX;
						res |= bit << 7 - minX;
						ctx = ((ctx & 0xefb) << 1) | bit | ((ln1 >> toShift) & 0x8)
								| ((ln2 >> toShift) & 0x200);
					}
					regBmp.bitmap[dst++] = (byte)res;
				}
			}
			void DecodeTemplate2(int line, int pad, int dst, int src, int width, int stride) {
				int ln1 = (line >= 1) ? regBmp.GetInt(src) : 0,
					ln2 = (line >= 2) ? regBmp.GetInt(src - stride) << 4 : 0,
					ctx = ((ln1 >> 3) & 0x7c) | ((ln2 >> 3) & 0x380), nxtByte;
				for (int x = 0; x < pad; x = nxtByte, src++) {
					nxtByte = x + 8;
					int minorWidth = width - x > 8 ? 8 : width - x, res = 0;	/* 6.2.5.7 3d */
					if (line >= 1)
						ln1 = (ln1 << 8)
							| (nxtByte < width ? regBmp.GetInt(src + 1) : 0);
					if (line >= 2)
						ln2 = (ln2 << 8)
							| (nxtByte < width ? regBmp.GetInt(src - stride + 1) << 4 : 0);
					for (int minX = 0; minX < minorWidth; minX++) {
						int cxCtx = !fOverride ? ctx : OverrideAtTemplate2(ctx, x + minX, line, res, minX),
							bit = arithDecoder.DecodeBit(cxCtx, cx), toShift = 10 - minX;
						res |= bit << (7 - minX);
						ctx = ((ctx & 0x1bd) << 1) | bit | ((ln1 >> toShift) & 0x4) | ((ln2 >> toShift) & 0x80);
					}
					regBmp.bitmap[dst++] = (byte)res;
				}
			}
			void DecodeTemplate3(int line, int pad, int dst, int src) {
				int ln1 = (line >= 1) ? regBmp.GetInt(src) : 0;
				int width = regBmp.width, ctx = (ln1 >> 1) & 0x70, nxtByte;
				for (int x = 0; x < pad; x = nxtByte, src++) {
					nxtByte = x + 8;
					int minorWidth = width - x > 8 ? 8 : width - x, res = 0;	/* 6.2.5.7 3d */
					if (line >= 1)
						ln1 = (ln1 << 8) | (nxtByte < width ? regBmp.GetInt(src + 1) : 0);
					for (int minX = 0; minX < minorWidth; minX++) {
						int cxCtx = !fOverride ? ctx : OverrideAtTemplate3(ctx, x + minX, line, res, minX),
							bit = arithDecoder.DecodeBit(cxCtx, cx);
						res |= bit << (7 - minX);
						ctx = ((ctx & 0x1f7) << 1) | bit | ((ln1 >> (8 - minX)) & 0x010);
					}
					regBmp.bitmap[dst++] = (byte)res;
				}
			}
			void UpdateOverrideFlags() {
				if (gbAtX == null || gbAtY == null) return;
				if (gbAtX.Length != gbAtY.Length) return;
				gbAtOverride = new bool[gbAtX.Length];
				switch (gbTemplate) {
					case 0:
						if (!useExtTemplates) {
							if (gbAtX[0] != 3  || gbAtY[0] != -1) SetOverrideFlag(0);
							if (gbAtX[1] != -3 || gbAtY[1] != -1) SetOverrideFlag(1);
							if (gbAtX[2] != 2  || gbAtY[2] != -2) SetOverrideFlag(2);
							if (gbAtX[3] != -2 || gbAtY[3] != -2) SetOverrideFlag(3);
						}
						else {
							if (gbAtX[0] != -2 || gbAtY[0] !=  0) SetOverrideFlag(0);
							if (gbAtX[1] !=  0 || gbAtY[1] != -2) SetOverrideFlag(1);
							if (gbAtX[2] != -2 || gbAtY[2] != -1) SetOverrideFlag(2);
							if (gbAtX[3] != -1 || gbAtY[3] != -2) SetOverrideFlag(3);
							if (gbAtX[4] !=  1 || gbAtY[4] != -2) SetOverrideFlag(4);
							if (gbAtX[5] !=  2 || gbAtY[5] != -1) SetOverrideFlag(5);
							if (gbAtX[6] != -3 || gbAtY[6] !=  0) SetOverrideFlag(6);
							if (gbAtX[7] != -4 || gbAtY[7] !=  0) SetOverrideFlag(7);
							if (gbAtX[8] !=  2 || gbAtY[8] != -2) SetOverrideFlag(8);
							if (gbAtX[9] !=  3 || gbAtY[9] != -1) SetOverrideFlag(9);
							if (gbAtX[10]!= -2 || gbAtY[10]!= -2) SetOverrideFlag(10);
							if (gbAtX[11]!= -3 || gbAtY[11]!= -1) SetOverrideFlag(11);
						}
						break;
					case 1: if (gbAtX[0] != 3 || gbAtY[0] != -1) SetOverrideFlag(0); break;
					case 2: if (gbAtX[0] != 2 || gbAtY[0] != -1) SetOverrideFlag(0); break;
					case 3: if (gbAtX[0] != 2 || gbAtY[0] != -1) SetOverrideFlag(0); break;
				}
			}
			void SetOverrideFlag(int index) {
				gbAtOverride[index] = fOverride = true;
			}
			int OverrideAtTemplate0a(int ctx, int x, int y, int res, int minX, int toShift) {
				if (gbAtOverride[0])
					ctx = (ctx & 0xffef) | (gbAtY[0] == 0 && gbAtX[0] >= -minX
								? (res >> (toShift - gbAtX[0]) & 0x1) << 4
								: regBmp.GetPixel(x + gbAtX[0], y + gbAtY[0]) << 4);
				if (gbAtOverride[1])
					ctx = (ctx & 0xfbff) | (gbAtY[1] == 0 && gbAtX[1] >= -minX
								? (res >> (toShift - gbAtX[1]) & 0x1) << 10
								: regBmp.GetPixel(x + gbAtX[1], y + gbAtY[1]) << 10);
				if (gbAtOverride[2])
					ctx = (ctx & 0xf7ff) | (gbAtY[2] == 0 && gbAtX[2] >= -minX
								? (res >> (toShift - gbAtX[2]) & 0x1) << 11
								: regBmp.GetPixel(x + gbAtX[2], y + gbAtY[2]) << 11);
				if (gbAtOverride[3])
					ctx = (ctx & 0x7fff) | (gbAtY[3] == 0 && gbAtX[3] >= -minX
								? (res >> (toShift - gbAtX[3]) & 0x1) << 15
								: regBmp.GetPixel(x + gbAtX[3], y + gbAtY[3]) << 15);
				return ctx;
			}
			int OverrideAtTemplate0b(int ctx, int x, int y, int res, int minX, int shft) {
				if (gbAtOverride[0])
					ctx = (ctx & 0xfffd) | (gbAtY[0] == 0 && gbAtX[0] >= -minX
								? (res >> (shft - gbAtX[0]) & 0x1) << 1
								: regBmp.GetPixel(x + gbAtX[0], y + gbAtY[0]) << 1);
				if (gbAtOverride[1])
					ctx = (ctx & 0xdfff) | (gbAtY[1] == 0 && gbAtX[1] >= -minX
								? (res >> (shft - gbAtX[1]) & 0x1) << 13
								: regBmp.GetPixel(x + gbAtX[1], y + gbAtY[1]) << 13);
				if (gbAtOverride[2])
					ctx = (ctx & 0xfdff) | (gbAtY[2] == 0 && gbAtX[2] >= -minX
								? (res >> (shft - gbAtX[2]) & 0x1) << 9
								: regBmp.GetPixel(x + gbAtX[2], y + gbAtY[2]) << 9);
				if (gbAtOverride[3])
					ctx = (ctx & 0xbfff) | (gbAtY[3] == 0 && gbAtX[3] >= -minX
								? (res >> (shft - gbAtX[3]) & 0x1) << 14
								: regBmp.GetPixel(x + gbAtX[3], y + gbAtY[3]) << 14);
				if (gbAtOverride[4])
					ctx = (ctx & 0xefff) | (gbAtY[4] == 0 && gbAtX[4] >= -minX
								? (res >> (shft - gbAtX[4]) & 0x1) << 12
								: regBmp.GetPixel(x + gbAtX[4], y + gbAtY[4]) << 12);
				if (gbAtOverride[5])
					ctx = (ctx & 0xffdf) | (gbAtY[5] == 0 && gbAtX[5] >= -minX
								? (res >> (shft - gbAtX[5]) & 0x1) << 5
								: regBmp.GetPixel(x + gbAtX[5], y + gbAtY[5]) << 5);
				if (gbAtOverride[6])
					ctx = (ctx & 0xfffb) | (gbAtY[6] == 0 && gbAtX[6] >= -minX
								? (res >> (shft - gbAtX[6]) & 0x1) << 2
								: regBmp.GetPixel(x + gbAtX[6], y + gbAtY[6]) << 2);
				if (gbAtOverride[7])
					ctx = (ctx & 0xfff7) | (gbAtY[7] == 0 && gbAtX[7] >= -minX
								? (res >> (shft - gbAtX[7]) & 0x1) << 3
								: regBmp.GetPixel(x + gbAtX[7], y + gbAtY[7]) << 3);
				if (gbAtOverride[8])
					ctx = (ctx & 0xf7ff) | (gbAtY[8] == 0 && gbAtX[8] >= -minX
								? (res >> (shft - gbAtX[8]) & 0x1) << 11
								: regBmp.GetPixel(x + gbAtX[8], y + gbAtY[8]) << 11);
				if (gbAtOverride[9])
					ctx = (ctx & 0xffef) | (gbAtY[9] == 0 && gbAtX[9] >= -minX
								? (res >> (shft - gbAtX[9]) & 0x1) << 4
								: regBmp.GetPixel(x + gbAtX[9], y + gbAtY[9]) << 4);
				if (gbAtOverride[10])
					ctx = (ctx & 0x7fff) | (gbAtY[10] == 0 && gbAtX[10] >= -minX
								? (res >> (shft - gbAtX[10]) & 0x1) << 15
								: regBmp.GetPixel(x + gbAtX[10], y + gbAtY[10]) << 15);
				if (gbAtOverride[11])
					ctx = (ctx & 0xfdff) | (gbAtY[11] == 0 && gbAtX[11] >= -minX
								? (res >> (shft - gbAtX[11]) & 0x1) << 10
								: regBmp.GetPixel(x + gbAtX[11], y + gbAtY[11]) << 10);
				return ctx;
			}
			int OverrideAtTemplate1(int ctx, int x, int y, int res, int minX) {
				ctx &= 0x1ff7;
				return (gbAtY[0] == 0 && gbAtX[0] >= -minX)
					? (ctx | (res >> (7 - (minX + gbAtX[0])) & 0x1) << 3)
					: (ctx | regBmp.GetPixel(x + gbAtX[0], y + gbAtY[0]) << 3);
			}
			int OverrideAtTemplate2(int ctx, int x, int y, int res, int minX) {
				ctx &= 0x3fb;
				return (gbAtY[0] == 0 && gbAtX[0] >= -minX)
					? (ctx | (res >> (7 - (minX + gbAtX[0])) & 0x1) << 2)
					: (ctx | regBmp.GetPixel(x + gbAtX[0], y + gbAtY[0]) << 2);
			}
			int OverrideAtTemplate3(int ctx, int x, int y, int res, int minX) {
				ctx &= 0x3ef;
				return (gbAtY[0] == 0 && gbAtX[0] >= -minX)
					? (ctx | (res >> (7 - (minX + gbAtX[0])) & 0x1) << 4)
					: (ctx | regBmp.GetPixel(x + gbAtX[0], y + gbAtY[0]) << 4);
			}
			internal void SetParams(long dataOffset, long dataLength, int gbh, int gbw) {
				this.isMMR = true;
				this.dataOffset = dataOffset;
				this.dataLength = dataLength;
				this.regInfo.height = gbh;
				this.regInfo.width = gbw;
				this.mmrDecompressor = null;
				this.regBmp = null;
			}
			internal void SetParams(int sdTemplate, short[] sdATX, short[] sdATY, 
					int symWidth, int hcHeight, CXStats cx, ArithmDecoder arithDecoder) {
				this.isTPGDon = this.isMMR = false;
				this.gbTemplate = sdTemplate;
				this.gbAtX = sdATX;
				this.gbAtY = sdATY;
				this.regInfo.width = symWidth;
				this.regInfo.height = hcHeight;
				this.cx = cx ?? this.cx;
				this.arithDecoder = arithDecoder ?? this.arithDecoder;
				this.mmrDecompressor = null;
				this.regBmp = null;
			}
			internal void SetParams(SegmentData seg, bool mmr, int gbh, int gbw, int gbTemplate, short[] gbAtX, short[] gbAtY) {
				this.dataOffset = seg.dataOffset;
				this.dataLength = seg.dataLength;
				this.isMMR = mmr;
				this.regInfo = new RegSegInfo { height = gbh, width = gbw };
				this.gbTemplate = gbTemplate;
				this.isTPGDon = false;
				this.gbAtX = gbAtX;
				this.gbAtY = gbAtY;
			}
			internal override void Init(SegmentHeader header, ImgStream sis) {
				base.Init(header, sis);
				int flags = imgStrm.ReadByte();
				useExtTemplates = (flags & 0x10) != 0;      /* Bit 4 */
				isTPGDon		= (flags & 0x08) != 0;      /* Bit 3 */
				gbTemplate		= (flags >> 1) & 3;			/* Bit 1-2 */
				isMMR			= (flags & 1) != 0;         /* Bit 0 */
				if (!isMMR) {
					int amtOfGbAt = (gbTemplate == 0) ? (useExtTemplates ? 12 : 4) : 1;
					(gbAtX, gbAtY) = (new short[amtOfGbAt], new short[amtOfGbAt]);
					for (int i = 0; i < amtOfGbAt; i++) 
						(gbAtX[i], gbAtY[i]) = (imgStrm.ReadSByte(), imgStrm.ReadSByte());
				}
				dataLength = imgStrm.Length - (dataOffset = imgStrm.Position);
			}
		}
		internal class JBIG2Page {
			internal Dictionary<int, SegmentHeader> segments = new Dictionary<int, SegmentHeader>();
			JB2Bmp	pgBmp;
			int		pageNumber;
			PBoxJBig2 document;
			internal JBIG2Page(PBoxJBig2 document, int pageNumber) {
				this.document = document;
				this.pageNumber = pageNumber;
			}
			internal SegmentHeader GetSegment(int number) {
				if (segments.ContainsKey(number))
					return segments[number];
				return document?.GetGlobalSegment(number);
			}
			internal JB2Bmp GetBitmap() {
				HashSet<int> regTypes = new HashSet<int> { 6, 7, 22, 23, 38, 39, 42, 43 };
				if (null != pgBmp || pageNumber < 1)
					return pgBmp;
				PageInformation pi = (PageInformation)segments
							.Values.FirstOrDefault(s => s.segType == 48).GetSegData();
				if (!pi.isStriped || pi.height != -1) {
					pgBmp = new JB2Bmp(pi.width, pi.height, pi.defPix);
					foreach (SegmentHeader s in segments.Values)
						if (regTypes.Contains(s.segType)) {
							Region r = (Region)s.GetSegData();
							JB2Bmp bmp = r.GetRegBmp();
							if (segments.Values.Where(x => regTypes.Contains(x.segType)).Count() == 1
							&& !pi.defPix && pi.width == bmp.width && pi.height == bmp.height)
								pgBmp = bmp;
							else {
								RegSegInfo ri = r.regInfo;
								bmp.blit(pgBmp, ri.xLoc, ri.yLoc, pi.GetComboOper(ri));
							}
						}
				}
				else {
					int line1 = 0, lineN = segments.Values.Where(x => x.segType == 50)
								.Select(x => ((EndOfStripe)x.GetSegData()).lineNum + 1).Last();
					pgBmp = new JB2Bmp(pi.width, lineN);
					foreach (SegmentHeader s in segments.Values)
						if (s.segType == 50) 
							line1 = ((EndOfStripe)s.GetSegData()).lineNum + 1;
						else if (regTypes.Contains(s.segType)) {
							Region r = (Region)s.GetSegData();
							RegSegInfo ri = r.regInfo;
							r.GetRegBmp().blit(pgBmp, ri.xLoc, line1, pi.GetComboOper(ri));
						}
				}
				segments = null;                // allow GC
				return pgBmp;
			}
		}
		Dictionary<int, JBIG2Page> pages = new Dictionary<int, JBIG2Page>();
		internal Dictionary<int, SegmentHeader> globSegs = new Dictionary<int, SegmentHeader>();
		internal SegmentHeader GetGlobalSegment(int segmentNr) {
			return globSegs?[segmentNr];
		}
		internal JBIG2Page GetPage(int pageNumber) {
			return pages.ContainsKey(pageNumber) ? pages[pageNumber] : null;
		}
		void MapStream() {
			List<SegmentHeader> segs = new List<SegmentHeader>();
			long offset = 0;
			bool typeRand = false;
			bufStr.Seek(0, SeekOrigin.Begin);
			int[] sig = { 0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A };
			if (sig.All(x => x == bufStr.ReadByte())) {
				int flags = bufStr.ReadByte();
				bool fPgNums = (flags & 2) != 0;		// numPagesKnown 
				typeRand = 0 == (flags & 1);			// Bit 0 - Indicates file organisation type
				if (!fPgNums)                           // D.4.3 Number of pages (field is only present 
					bufStr.ReadInt();                   // if amount of pages are 'NOT unknown')
				offset = bufStr.Position;
			}
			globSegs = globSegs ?? new Dictionary<int, SegmentHeader>();
			for (int segmentType = 0; segmentType != 51 && !bufStr.EOF(4); bufStr.Seek(offset, SeekOrigin.Begin)) {
				SegmentHeader segment = new SegmentHeader(this, bufStr, offset);
				segmentType = segment.segType;
				if (segment.pgAssoc != 0) {
					JBIG2Page page = GetPage(segment.pgAssoc);
					if (page == null)
						pages[segment.pgAssoc] = page = new JBIG2Page(this, segment.pgAssoc);
					page.segments[segment.segNo] = segment;
				}
				else
					globSegs[segment.segNo] = segment;
				segs.Add(segment);
				offset = bufStr.Position;
				if (!typeRand)
					offset += segment.DataLen;
			}
			if (typeRand)                                           // Random: Data part starts after all the headers
				foreach (SegmentHeader s in segs) {
					s.DataStart = offset;
					offset += s.DataLen;
				}
		}
		public PBoxJBig2(byte[] input, byte[] glob = null) {
			if (input == null)
				throw new Exception("imageInputStream must not be null");
			bufStr = new ImgStream(input);
			if (glob != null)
				globSegs = new PBoxJBig2(glob).globSegs;
			MapStream();
		}
		public Image decodeImage() {
			return GetPage(1).GetBitmap().ToImage();
		}
	}
}
