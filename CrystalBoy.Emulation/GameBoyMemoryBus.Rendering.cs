﻿#region Copyright Notice
// This file is part of CrystalBoy.
// Copyright © 2008-2011 Fabien Barbier
// 
// CrystalBoy is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// CrystalBoy is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
#endregion

using System;
using System.Collections.Generic;
using CrystalBoy.Core;

namespace CrystalBoy.Emulation
{
	public sealed partial class GameBoyMemoryBus
	{
		#region Variables

		private FrameEventArgs frameEventArgs;
		private VideoRenderer videoRenderer;
		private MemoryBlock renderPaletteMemoryBlock;

		private unsafe uint** backgroundPalettes32, spritePalettes32;
		private unsafe ushort** backgroundPalettes16, spritePalettes16;

		private uint[] backgroundPalette;
		private uint[] objectPalette1;
		private uint[] objectPalette2;

		private bool greyPaletteUpdated;

		#endregion

		#region Events

		private EventHandler<FrameEventArgs> beforeRendering;

		public event EventHandler<FrameEventArgs> BeforeRendering
		{
			add { beforeRendering += value; }
			remove
			{
				beforeRendering -= value;
				if (beforeRendering != null) frameEventArgs.Reset();
			}
		}

		public event EventHandler AfterRendering;

		#endregion

		#region Initialize

		partial void InitializeRendering()
		{
			frameEventArgs = new FrameEventArgs();

			unsafe
			{
				uint** pointerTable;
				uint* paletteTable;

				// We will allocate memory for 16 palettes of 4 colors each, and for a palette pointer table of 16 pointers
				renderPaletteMemoryBlock = new MemoryBlock(2 * 8 * sizeof(uint*) + 2 * 8 * 4 * sizeof(uint));

				pointerTable = (uint**)renderPaletteMemoryBlock.Pointer; // Take 16 uint* at the beginning for pointer table
				paletteTable = (uint*)(pointerTable + 16); // Take the rest for palette array

				// Fill the pointer table with palette
				for (int i = 0; i < 16; i++)
					pointerTable[i] = paletteTable + 4 * i; // Each palette is 4 uint wide

				backgroundPalettes32 = pointerTable; // First 8 pointers are for the 8 background palettes
				spritePalettes32 = backgroundPalettes32 + 8; // Other 8 pointers are for the 8 sprite palettes

				// We'll use the same memory for 16 and 32 bit palettes, because only one will be used at once
				backgroundPalettes16 = (ushort**)backgroundPalettes32;
				spritePalettes16 = backgroundPalettes16 + 8;
			}

			backgroundPalette = new uint[4];
			objectPalette1 = new uint[4];
			objectPalette2 = new uint[4];
		}

		#endregion

		#region Reset

		partial void ResetRendering()
		{
			if (!colorHardware || useBootRom)
			{
				greyPaletteUpdated = false;
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, backgroundPalette, 0, 4 * sizeof(uint));
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, objectPalette1, 0, 4 * sizeof(uint));
				Buffer.BlockCopy(LookupTables.GrayPalette, 0, objectPalette2, 0, 4 * sizeof(uint));
			}
			if (videoRenderer != null)
			{
				videoRenderer.Reset();
				RenderBorder();
				RenderVideoFrame();
			}
		}

		#endregion

		#region Dispose

		partial void DisposeRendering()
		{
			renderPaletteMemoryBlock.Dispose();
		}

		#endregion

		#region Properties

		[CLSCompliant(false)]
		public VideoRenderer VideoRenderer
		{
			get { return videoRenderer; }
			set
			{
				videoRenderer = value;
				ClearBuffer();
			}
		}

		#endregion

		#region Rendering

		private unsafe void RenderVideoFrame()
		{
			if (videoRenderer == null)
			{
#if WITH_THREADING
				// Clear the flag because we didn't draw anything at all…
				isRenderingVideo = false;
#endif
				return;
			}

			if (savedVideoStatusSnapshot.SuperGameBoyScreenStatus != 1)
			{
				var buffer = videoRenderer.LockScreenBuffer();

				if (savedVideoStatusSnapshot.SuperGameBoyScreenStatus == 0 && (savedVideoStatusSnapshot.LCDC & 0x80) != 0)
				{
					if (colorMode)
					{
						FillPalettes32((ushort*)savedVideoStatusSnapshot.PaletteMemory);
						DrawColorFrame32((byte*)buffer.DataPointer, buffer.Stride);
					}
					else
					{
						if (greyPaletteUpdated)
						{
							FillPalettes32((ushort*)paletteMemory);
							for (int i = 0; i < backgroundPalette.Length; i++)
								backgroundPalette[i] = backgroundPalettes32[0][i];
							for (int i = 0; i < objectPalette1.Length; i++)
								objectPalette1[i] = spritePalettes32[0][i];
							for (int i = 0; i < objectPalette2.Length; i++)
								objectPalette2[i] = spritePalettes32[1][i];
							greyPaletteUpdated = false;
						}
						DrawFrame32((byte*)buffer.DataPointer, buffer.Stride);
					}
#if WITH_THREADING
					// Clear the flag once the real drawing job is done, as we don't need the buffer anymore
					isRenderingVideo = false;
#endif
				}
				else
				{
					uint clearColor = savedVideoStatusSnapshot.SuperGameBoyScreenStatus == 2 ?
						0xFF000000 :
						savedVideoStatusSnapshot.SuperGameBoyScreenStatus == 3 ?
							LookupTables.StandardColorLookupTable32[videoRenderer.ClearColor] :
							0xFFFFFFFF;

#if WITH_THREADING
					// Clear the flag before drawing anything, as we don't need the saved state
					isRenderingVideo = false;
#endif
					ClearBuffer32((byte*)buffer.DataPointer, buffer.Stride, clearColor);
				}

				videoRenderer.UnlockScreenBuffer();
			}
			else isRenderingVideo = false; // Clear the flag as we didn't have any use for the saved sate

			videoRenderer.Render();
		}

		private unsafe void RenderBorder()
		{
			try
			{
				var buffer = videoRenderer.LockBorderBuffer();

				DrawBorder32((byte*)buffer.DataPointer, buffer.Stride);

				videoRenderer.UnlockBorderBuffer();
			}
			catch (NotSupportedException) { }
			catch (NotImplementedException) { }
		}

		#region Palette Initialization

		private unsafe void FillPalettes16(ushort* paletteData)
		{
			ushort* dest = backgroundPalettes16[0];

			for (int i = 0; i < 64; i++)
				*dest++ = LookupTables.StandardColorLookupTable16[*paletteData++];
		}

		private unsafe void FillPalettes32(ushort* paletteData)
		{
			uint* dest = backgroundPalettes32[0];

			for (int i = 0; i < 64; i++)
				*dest++ = LookupTables.StandardColorLookupTable32[*paletteData++];
		}

		#endregion

		#region Event Handling

		private void OnBeforeRendering(FrameEventArgs e)
		{
			if (beforeRendering != null)
				beforeRendering(this, e);
		}

		private void OnAfterRendering(EventArgs e)
		{
			if (AfterRendering != null)
				AfterRendering(this, e);
		}

		#endregion

		#region Buffer Clearing

		public unsafe void ClearBuffer()
		{
			if (videoRenderer == null)
				return;

			var buffer = videoRenderer.LockScreenBuffer();

			ClearBuffer32((byte*)buffer.DataPointer, buffer.Stride, 0xFF000000);

			videoRenderer.UnlockScreenBuffer();

			videoRenderer.Render();
		}

		#region 16 BPP

		private unsafe void ClearBuffer16(byte* buffer, int stride, ushort color)
		{
			ushort* bufferPixel;

			for (int i = 0; i < 144; i++)
			{
				bufferPixel = (ushort*)buffer;

				for (int j = 0; j < 160; j++)
					*bufferPixel++ = color;

				buffer += stride;
			}
		}

		#endregion

		#region 32 BPP

		private unsafe void ClearBuffer32(byte* buffer, int stride, uint color)
		{
			uint* bufferPixel;

			for (int i = 0; i < 144; i++)
			{
				bufferPixel = (uint*)buffer;

				for (int j = 0; j < 160; j++)
					*bufferPixel++ = color;

				buffer += stride;
			}
		}

		#endregion

		#endregion

		#region ObjectData Structure

		struct ObjectData
		{
			public int Left;
			public int Right;
			public int PixelData;
			public int Palette;
			public bool Priority;
		}

		ObjectData[] objectData = new ObjectData[10];

		#endregion

		#region Border Rendering

		/// <summary>Draws the SGB border into a 32 BPP buffer.</summary>
		/// <param name="buffer">Destination pixel buffer.</param>
		/// <param name="stride">Buffer line stride.</param>
		private unsafe void DrawBorder32(byte* buffer, int stride)
		{
			uint[] paletteData = new uint[8 << 4];
			int mapRowOffset = -32;

			// Fill only the 4 border palettes… Just ignore the others
			for (int i = 0x40; i < paletteData.Length; i++) paletteData[i] = LookupTables.StandardColorLookupTable32[sgbBorderMapData[0x400 - 0x40 + i]];

			for (int i = 0; i < 224; i++)
			{
				uint* pixelPointer = (uint*)buffer;
				int tileBaseRowOffset = (i & 0x7) << 1; // Tiles are stored in a weird planar way…
				int mapTileOffset = tileBaseRowOffset != 0 ? mapRowOffset : mapRowOffset += 32;

				for (int j = 32; j-- != 0; mapTileOffset++)
				{
					ushort tileInformation = sgbBorderMapData[mapTileOffset];
					int tileRowOffset = ((tileInformation & 0xFF) << 5) + ((tileInformation & 0x8000) != 0 ? 0xE - tileBaseRowOffset : tileBaseRowOffset);
					int paletteOffset = ((tileInformation >> 10) & 0x7) << 4;

					byte tileValue0 = sgbCharacterData[tileRowOffset];
					byte tileValue1 = sgbCharacterData[tileRowOffset + 1];
					byte tileValue2 = sgbCharacterData[tileRowOffset + 16];
					byte tileValue3 = sgbCharacterData[tileRowOffset + 17];

					if ((tileInformation & 0x4000) != 0)
						for (byte k = 0x01; k != 0; k <<= 1)
						{
							byte color = (tileValue0 & k) != 0 ? (byte)1 : (byte)0;
							if ((tileValue1 & k) != 0) color |= 2;
							if ((tileValue2 & k) != 0) color |= 4;
							if ((tileValue3 & k) != 0) color |= 8;
							*pixelPointer++ = color != 0 ? paletteData[paletteOffset + color] : 0;
						}
					else
						for (byte k = 0x80; k != 0; k >>= 1)
						{
							byte color = (tileValue0 & k) != 0 ? (byte)1 : (byte)0;
							if ((tileValue1 & k) != 0) color |= 2;
							if ((tileValue2 & k) != 0) color |= 4;
							if ((tileValue3 & k) != 0) color |= 8;
							*pixelPointer++ = color != 0 ? paletteData[paletteOffset + color] : 0;
						}
				}

				buffer += stride;
			}
		}

		#endregion

		#region Color Rendering

		#region 32 BPP

		/// <summary>Draws the current frame into a 32 BPP buffer.</summary>
		/// <param name="buffer">Destination pixel buffer.</param>
		/// <param name="stride">Buffer line stride.</param>
		private unsafe void DrawColorFrame32(byte* buffer, int stride)
		{
			// WARNING: Very looooooooooong code :D
			// I have to keep track of a lot of variables for this one-pass rendering
			// Since on GBC the priorities between BG, WIN and OBJ can sometimes be weird, I don't think there is a better way of handling this.
			// The code may lack some optimizations tough, but i try my best to keep the variable count the lower possible (taking in account the fact that MS JIT is designed to handle no more than 64 variables...)
			// If you see some possible optimization, feel free to contribute.
			// The code might be very long but it is still very well structured, so with a bit of knowledge on (C)GB hardware you should understand it easily
			// In fact I think the function works pretty much like the real lcd controller on (C)GB... ;)
			byte* bufferLine = buffer;
			uint* bufferPixel;
			int scx, scy, wx, wy;
			int pi, ppi, data1, data2;
			bool bgPriority, tilePriority, winDraw, winDraw2, objDraw, signedIndex;
			byte objDrawn; 
			uint** bgPalettes, objPalettes;
			uint* tilePalette;
			byte* bgMap, winMap,
				bgTile, winTile;
			int bgLineOffset, winLineOffset;
			int bgTileIndex, pixelIndex;
			ushort* bgTiles;
			int i, j;
			int objHeight, objCount;
			uint objColor = 0;

			bgPalettes = this.backgroundPalettes32;
			objPalettes = this.spritePalettes32;

			fixed (ObjectData* objectData = this.objectData)
			fixed (ushort* paletteIndexTable = LookupTables.PaletteLookupTable,
				flippedPaletteIndexTable = LookupTables.FlippedPaletteLookupTable)
			{
				tilePalette = bgPalettes[0];

				data1 = savedVideoStatusSnapshot.LCDC;
				bgPriority = (data1 & 0x01) != 0;
				bgMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
				winDraw = (data1 & 0x20) != 0;
				winMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
				objDraw = (data1 & 0x02) != 0;
				objHeight = (data1 & 0x04) != 0 ? 16 : 8;
				signedIndex = (data1 & 0x10) == 0;
				bgTiles = (ushort*)(signedIndex ? savedVideoStatusSnapshot.VideoMemory + 0x1000 : savedVideoStatusSnapshot.VideoMemory);

				scx = savedVideoStatusSnapshot.SCX;
				scy = savedVideoStatusSnapshot.SCY;
				wx = savedVideoStatusSnapshot.WX - 7;
				wy = savedVideoStatusSnapshot.WY;

				tilePriority = false;

				pi = 0; // Port access list index
				ppi = 0; // Palette access list index

				for (i = 0; i < 144; i++) // Loop on frame lines
				{
					#region Video Port Updates

					data2 = i * 456; // Line clock

					// Update ports before drawing the line
					while (pi < savedVideoPortAccessList.Count && savedVideoPortAccessList[pi].Clock <= data2)
					{
						switch (savedVideoPortAccessList[pi].Port)
						{
							case Port.LCDC:
								data1 = savedVideoPortAccessList[pi].Value;
								bgPriority = (data1 & 0x01) != 0;
								bgMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
								winDraw = (data1 & 0x20) != 0;
								winMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
								objDraw = (data1 & 0x02) != 0;
								objHeight = (data1 & 0x04) != 0 ? 16 : 8;
								signedIndex = (data1 & 0x10) == 0;
								bgTiles = (ushort*)(signedIndex ? savedVideoStatusSnapshot.VideoMemory + 0x1000 : savedVideoStatusSnapshot.VideoMemory);
								break;
							case Port.SCX: scx = savedVideoPortAccessList[pi].Value; break;
							case Port.SCY: scy = savedVideoPortAccessList[pi].Value; break;
							case Port.WX: wx = savedVideoPortAccessList[pi].Value - 7; break;
						}

						pi++;
					}
					// Update palettes before drawing the line (This is necessary for a lot of demos with dynamic palettes)
					while (ppi < savedPaletteAccessList.Count && savedPaletteAccessList[ppi].Clock < data2)
					{
						// By doing this, we trash the palette memory snapshot… But at least it works. (Might be necessary to allocate another temporary palette buffer in the future)
						savedVideoStatusSnapshot.PaletteMemory[savedPaletteAccessList[ppi].Offset] = savedPaletteAccessList[ppi].Value;
						bgPalettes[0][savedPaletteAccessList[ppi].Offset / 2] = LookupTables.StandardColorLookupTable32[((ushort*)savedVideoStatusSnapshot.PaletteMemory)[savedPaletteAccessList[ppi].Offset / 2]];

						ppi++;
					}

					#endregion

					#region Object Attribute Memory Search

					// Find valid sprites for the line, limited to 10 like on real GB
					for (j = 0, objCount = 0; j < 40 && objCount < 10; j++) // Loop on OAM data
					{
						bgTile = savedVideoStatusSnapshot.ObjectAttributeMemory + (j << 2); // Obtain a pointer to the object data

						// First byte is vertical position and that's exactly what we want to compare :)
						data1 = *bgTile - 16;
						if (data1 <= i && data1 + objHeight > i) // Check that the sprite is drawn on the current line
						{
							// Initialize the object data according to what we want
							data2 = bgTile[1]; // Second byte is the horizontal position, we store it somewhere
							objectData[objCount].Left = data2 - 8;
							objectData[objCount].Right = data2;
							data2 = bgTile[3]; // Fourth byte contain flags that we'll examine
							objectData[objCount].Palette = data2 & 0x7; // Use the palette index stored in flags
							objectData[objCount].Priority = (data2 & 0x80) == 0; // Store the priority information
							// Now we check the Y flip flag, as we'll use it to calculate the tile line offset
							if ((data2 & 0x40) != 0)
								data1 = (objHeight + data1 - i - 1) << 1;
							else
								data1 = (i - data1) << 1;
							// Now that we have the line offset, we add to it the tile offset
							if (objHeight == 16) // Depending on the sprite size we'll have to mask bit 0 of the tile index
								data1 += (bgTile[2] & 0xFE) << 4; // Third byte is the tile index
							else
								data1 += bgTile[2] << 4; // A tile is 16 bytes wide
							// Now all that is left is to fetch the tile data :)
							if ((data2 & 0x8) != 0)
								bgTile = savedVideoStatusSnapshot.VideoMemory + data1 + 0x2000; // Calculate the full tile line address for VRAM Bank 1
							else
								bgTile = savedVideoStatusSnapshot.VideoMemory + data1; // Calculate the full tile line address for VRAM Bank 0
							// Depending on the X flip flag, we will load the flipped pixel data or the regular one
							if ((data2 & 0x20) != 0)
								objectData[objCount].PixelData = flippedPaletteIndexTable[*(ushort*)bgTile];
							else
								objectData[objCount].PixelData = paletteIndexTable[*(ushort*)bgTile];
							objCount++; // Increment the object counter
						}
					}

					#endregion

					#region Background and Window Fetch Initialization

					// Initialize the background and window with new parameters
					bgTileIndex = scx >> 3;
					pixelIndex = scx & 7;
					data1 = (scy + i) >> 3; // Background Line Index
					bgLineOffset = (scy + i) & 7;
					if (data1 >= 32) // Tile the background vertically
						data1 -= 32;
					bgTile = bgMap + (data1 << 5) + bgTileIndex;
					winTile = winMap + (((i - wy) << 2) & ~0x1F); // Optimisation for 32 * x / 8 => >> 3 << 5
					winLineOffset = (i - wy) & 7;

					winDraw2 = winDraw && i >= wy;

					#endregion

					// Adjust the current pixel to the current line
					bufferPixel = (uint*)bufferLine;

					// Do the actual drawing
					for (j = 0; j < 160; j++) // Loop on line pixels
					{
						objDrawn = 0; // Draw no object by default

						if (objDraw && objCount > 0)
						{
							for (data2 = 0; data2 < objCount; data2++)
							{
								if (objectData[data2].Left <= j && objectData[data2].Right > j)
								{
									objColor = (uint)(objectData[data2].PixelData >> ((j - objectData[data2].Left) << 1)) & 3;
									if ((objDrawn = (byte)(objColor != 0 ? !bgPriority || objectData[data2].Priority ? 2 : 1 : 0)) != 0)
									{
										objColor = objPalettes[objectData[data2].Palette][objColor];
										break;
									}
								}
							}
						}
						if (winDraw2 && j >= wx)
						{
							if (pixelIndex >= 8 || j == 0 || j == wx)
							{
								data2 = *(winTile + 0x2000);
								tilePalette = bgPalettes[data2 & 0x7];
								data1 = ((data2 & 0x40) != 0 ? 7 - winLineOffset : winLineOffset) + (signedIndex ? (sbyte)*winTile++ << 3 : *winTile++ << 3);
								if ((data2 & 0x8) != 0) data1 += 0x1000;
								data1 = (data2 & 0x20) != 0 ? flippedPaletteIndexTable[bgTiles[data1]] : paletteIndexTable[bgTiles[data1]];

								tilePriority = bgPriority && (data2 & 0x80) != 0;

								if (j == 0 && wx < 0)
								{
									pixelIndex = -wx;
									data1 >>= pixelIndex << 1;
								}
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (!(tilePriority || objDrawn == 1) || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];

							data1 >>= 2;
							pixelIndex++;
						}
						else
						{
							if (pixelIndex >= 8 || j == 0)
							{
								if (bgTileIndex++ >= 32) // Tile the background horizontally
								{
									bgTile -= 32;
									bgTileIndex = 0;
								}

								data2 = *(bgTile + 0x2000);
								tilePalette = bgPalettes[data2 & 0x7];
								data1 = ((data2 & 0x40) != 0 ? 7 - bgLineOffset : bgLineOffset) + (signedIndex ? (sbyte)*bgTile++ << 3 : *bgTile++ << 3);
								if ((data2 & 0x8) != 0) data1 += 0x1000;
								data1 = (data2 & 0x20) != 0 ? flippedPaletteIndexTable[bgTiles[data1]] : paletteIndexTable[bgTiles[data1]];

								tilePriority = bgPriority && (data2 & 0x80) != 0;

								if (j == 0 && pixelIndex > 0) data1 >>= pixelIndex << 1;
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (!(tilePriority || objDrawn == 1) || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];
							data1 >>= 2;
							pixelIndex++;
						}
					}

					bufferLine += stride;
				}
			}
		}

		#endregion

		#endregion

		#region Grayscale Rendering

		#region 32 BPP

		/// <summary>Draws the current frame into a 32 BPP buffer.</summary>
		/// <param name="buffer">Destination buffer.</param>
		/// <param name="stride">Buffer line stride.</param>
		private unsafe void DrawFrame32(byte* buffer, int stride)
		{
			// WARNING: Very looooooooooong code :D
			// I have to keep track of a lot of variables for this one-pass rendering
			// Since on GBC the priorities between BG, WIN and OBJ can sometimes be weird, I don't think there is a better way of handling this.
			// The code may lack some optimizations tough, but i try my best to keep the variable count the lower possible (taking in account the fact that MS JIT is designed to handle no more than 64 variables...)
			// If you see some possible optimization, feel free to contribute.
			// The code might be very long but it is still very well structured, so with a bit of knowledge on (C)GB hardware you should understand it easily
			// In fact I think the function works pretty much like the real lcd controller on (C)GB... ;)
			byte* bufferLine = buffer;
			uint* bufferPixel;
			int scx, scy, wx, wy;
			int pi, data1, data2;
			bool bgDraw, winDraw, winDraw2, objDraw, signedIndex;
			byte objDrawn;
			uint** bgPalettes, objPalettes;
			uint* tilePalette;
			byte* bgMap, winMap,
				bgTile, winTile;
			int bgLineOffset, winLineOffset;
			int bgTileIndex, pixelIndex;
			ushort* bgTiles;
			int i, j;
			int objHeight, objCount;
			uint objColor = 0;

			bgPalettes = this.backgroundPalettes32;
			objPalettes = this.spritePalettes32;

			fixed (ObjectData* objectData = this.objectData)
			fixed (ushort* paletteIndexTable = LookupTables.PaletteLookupTable,
				flippedPaletteIndexTable = LookupTables.FlippedPaletteLookupTable)
			{
				tilePalette = bgPalettes[0];

				data1 = savedVideoStatusSnapshot.LCDC;
				bgDraw = (data1 & 0x01) != 0;
				bgMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
				winDraw = (data1 & 0x20) != 0;
				winMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
				objDraw = (data1 & 0x02) != 0;
				objHeight = (data1 & 0x04) != 0 ? 16 : 8;
				signedIndex = (data1 & 0x10) == 0;
				bgTiles = (ushort*)(signedIndex ? savedVideoStatusSnapshot.VideoMemory + 0x1000 : savedVideoStatusSnapshot.VideoMemory);

				scx = savedVideoStatusSnapshot.SCX;
				scy = savedVideoStatusSnapshot.SCY;
				wx = savedVideoStatusSnapshot.WX - 7;
				wy = savedVideoStatusSnapshot.WY;
				data1 = savedVideoStatusSnapshot.BGP;
				for (i = 0; i < 4; i++)
				{
					tilePalette[i] = backgroundPalette[data1 & 3];
					data1 >>= 2;
				}
				data1 = savedVideoStatusSnapshot.OBP0;
				for (j = 0; j < 4; j++)
				{
					objPalettes[0][j] = objectPalette1[data1 & 3];
					data1 >>= 2;
				}
				data1 = savedVideoStatusSnapshot.OBP1;
				for (j = 0; j < 4; j++)
				{
					objPalettes[1][j] = objectPalette2[data1 & 3];
					data1 >>= 2;
				}

				pi = 0;

				for (i = 0; i < 144; i++) // Loop on frame lines
				{
					#region Video Port Updates

					data2 = i * 456; // Line clock

					// Update ports before drawing the line
					while (pi < savedVideoPortAccessList.Count && savedVideoPortAccessList[pi].Clock <= data2)
					{
						switch (savedVideoPortAccessList[pi].Port)
						{
							case Port.LCDC:
								data1 = savedVideoPortAccessList[pi].Value;
								bgDraw = (data1 & 0x01) != 0;
								bgMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x08) != 0 ? 0x1C00 : 0x1800);
								winDraw = (data1 & 0x20) != 0;
								winMap = savedVideoStatusSnapshot.VideoMemory + ((data1 & 0x40) != 0 ? 0x1C00 : 0x1800);
								objDraw = (data1 & 0x02) != 0;
								objHeight = (data1 & 0x04) != 0 ? 16 : 8;
								signedIndex = (data1 & 0x10) == 0;
								bgTiles = (ushort*)(signedIndex ? savedVideoStatusSnapshot.VideoMemory + 0x1000 : savedVideoStatusSnapshot.VideoMemory);
								break;
							case Port.SCX: scx = savedVideoPortAccessList[pi].Value; break;
							case Port.SCY: scy = savedVideoPortAccessList[pi].Value; break;
							case Port.WX: wx = savedVideoPortAccessList[pi].Value - 7; break;
							case Port.BGP:
								data1 = savedVideoPortAccessList[pi].Value;
								for (j = 0; j < 4; j++)
								{
									tilePalette[j] = backgroundPalette[data1 & 3];
									data1 >>= 2;
								}
								break;
							case Port.OBP0:
								data1 = savedVideoPortAccessList[pi].Value;
								for (j = 0; j < 4; j++)
								{
									objPalettes[0][j] = objectPalette1[data1 & 3];
									data1 >>= 2;
								}
								break;
							case Port.OBP1:
								data1 = savedVideoPortAccessList[pi].Value;
								for (j = 0; j < 4; j++)
								{
									objPalettes[1][j] = objectPalette2[data1 & 3];
									data1 >>= 2;
								}
								break;
						}

						pi++;
					}

					#endregion

					#region Object Attribute Memory Search

					// Find valid sprites for the line, limited to 10 like on real GB
					for (j = 0, objCount = 0; j < 40 && objCount < 10; j++) // Loop on OAM data
					{
						bgTile = savedVideoStatusSnapshot.ObjectAttributeMemory + (j << 2); // Obtain a pointer to the object data

						// First byte is vertical position and that's exactly what we want to compare :)
						data1 = *bgTile - 16;
						if (data1 <= i && data1 + objHeight > i) // Check that the sprite is drawn on the current line
						{
							// Initialize the object data according to what we want
							data2 = bgTile[1]; // Second byte is the horizontal position, we store it somewhere
							objectData[objCount].Left = data2 - 8;
							objectData[objCount].Right = data2;
							data2 = bgTile[3]; // Fourth byte contain flags that we'll examine
							objectData[objCount].Palette = (data2 & 0x10) != 0 ? 1 : 0; // Set the palette index according to the flags
							objectData[objCount].Priority = (data2 & 0x80) == 0; // Store the priority information
							// Now we check the Y flip flag, as we'll use it to calculate the tile line offset
							if ((data2 & 0x40) != 0)
								data1 = (objHeight + data1 - i - 1) << 1;
							else
								data1 = (i - data1) << 1;
							// Now that we have the line offset, we add to it the tile offset
							if (objHeight == 16) // Depending on the sprite size we'll have to mask bit 0 of the tile index
								data1 += (bgTile[2] & 0xFE) << 4; // Third byte is the tile index
							else
								data1 += bgTile[2] << 4; // A tile is 16 bytes wide
							// No all that is left is to fetch the tile data :)
							bgTile = savedVideoStatusSnapshot.VideoMemory + data1; // Calculate the full tile line address for VRAM Bank 0
							// Depending on the X flip flag, we will load the flipped pixel data or the regular one
							if ((data2 & 0x20) != 0)
								objectData[objCount].PixelData = flippedPaletteIndexTable[*(ushort*)bgTile];
							else
								objectData[objCount].PixelData = paletteIndexTable[*(ushort*)bgTile];
							objCount++; // Increment the object counter
						}
					}

					#endregion

					#region Background and Window Fetch Initialization

					// Initialize the background and window with new parameters
					bgTileIndex = scx >> 3;
					pixelIndex = scx & 7;
					data1 = (scy + i) >> 3; // Background Line Index
					bgLineOffset = (scy + i) & 7;
					if (data1 >= 32) // Tile the background vertically
						data1 -= 32;
					bgTile = bgMap + (data1 << 5) + bgTileIndex;
					winTile = winMap + (((i - wy) << 2) & ~0x1F);
					winLineOffset = (i - wy) & 7;

					winDraw2 = winDraw && i >= wy;

					#endregion

					// Adjust the current pixel to the current line
					bufferPixel = (uint*)bufferLine;

					// Do the actual drawing
					for (j = 0; j < 160; j++) // Loop on line pixels
					{
						objDrawn = 0; // Draw no object by default

						if (objDraw && objCount > 0)
						{
							for (data2 = 0; data2 < objCount; data2++)
							{
								if (objectData[data2].Left <= j && objectData[data2].Right > j)
								{
									objColor = (uint)(objectData[data2].PixelData >> ((j - objectData[data2].Left) << 1)) & 3;
									if ((objDrawn = (byte)(objColor != 0 ? objectData[data2].Priority ? 2 : 1 : 0)) != 0)
									{
										objColor = objPalettes[objectData[data2].Palette][objColor];
										break;
									}
								}
							}
						}
						if (winDraw2 && j >= wx)
						{
							if (pixelIndex >= 8 || j == 0 || j == wx)
							{
								data1 = winLineOffset + (signedIndex ? (sbyte)*winTile++ << 3 : *winTile++ << 3);

								data1 = paletteIndexTable[bgTiles[data1]];

								if (j == 0 && wx < 0)
								{
									pixelIndex = -wx;
									data1 >>= pixelIndex << 1;
								}
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (objDrawn == 2 || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];

							data1 >>= 2;
							pixelIndex++;
						}
						else if (bgDraw)
						{
							if (pixelIndex >= 8 || j == 0)
							{
								if (bgTileIndex++ >= 32) // Tile the background horizontally
								{
									bgTile -= 32;
									bgTileIndex = 0;
								}

								data1 = bgLineOffset + (signedIndex ? (sbyte)*bgTile++ << 3 : *bgTile++ << 3);

								data1 = paletteIndexTable[bgTiles[data1]];

								if (j == 0 && pixelIndex > 0) data1 >>= pixelIndex << 1;
								else pixelIndex = 0;
							}

							*bufferPixel++ = objDrawn != 0 && (objDrawn == 2 || (data1 & 0x3) == 0) ? objColor : tilePalette[data1 & 0x3];
							data1 >>= 2;
							pixelIndex++;
						}
						else *bufferPixel++ = objDrawn != 0 ? objColor : LookupTables.GrayPalette[0];
					}

					bufferLine += stride;
				}
			}
		}

		#endregion

		#endregion

		#endregion
	}
}
