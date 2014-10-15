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
using System.Text;

namespace CrystalBoy.Decompiler.Analyzer
{
	sealed class Label
	{
		int offset;
		string name;
		bool isFunction, analyzed;

		public Label(int offset)
			: this(offset, null)
		{
		}

		public Label(int offset, string name)
		{
			this.offset = offset;
			this.name = name;
		}

		public int Offset
		{
			get
			{
				return offset;
			}
		}

		public string Name
		{
			get
			{
				// Create and cache the name when needed
				if (name == null)
					name = "Label" + offset.ToString("X6");

				return name;
			}
		}

		public bool IsFunction
		{
			get
			{
				return isFunction;
			}
			set
			{
				isFunction = value;
			}
		}

		public bool Analyzed
		{
			get
			{
				return analyzed;
			}
			set
			{
				analyzed = value;
			}
		}
	}
}
