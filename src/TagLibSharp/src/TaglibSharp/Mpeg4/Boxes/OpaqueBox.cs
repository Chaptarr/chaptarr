//
// OpaqueBox.cs: Provides a box implementation that preserves its
// on-disk bytes exactly, with no re-parsing or re-rendering.
//
// This is used for atoms like the Nero chapter box (chpl) whose
// internal format has multiple versions. Re-rendering through the
// normal Box pipeline can silently corrupt the data. Preserving the
// complete atom bytes allows a lossless round-trip.
//
// Copyright (C) 2026
//
// This library is free software; you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License version
// 2.1 as published by the Free Software Foundation.
//
// This library is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// Lesser General Public License for more details.
//

using System;

namespace TagLib.Mpeg4
{
	/// <summary>
	///    A box that stores and renders its original on-disk bytes verbatim.
	/// </summary>
	public class OpaqueBox : Box
	{
		private readonly ByteVector _rawBytes;

		/// <summary>
		/// Initializes an opaque box by copying its complete on-disk bytes.
		/// </summary>
		/// <param name="header">The parsed box header.</param>
		/// <param name="file">The media file containing the box.</param>
		/// <param name="handler">The handler associated with the box.</param>
		public OpaqueBox(BoxHeader header, TagLib.File file, IsoHandlerBox handler)
			: base(header, handler)
		{
			if (file == null)
			{
				throw new ArgumentNullException(nameof(file));
			}

			file.Seek(header.Position);
			_rawBytes = file.ReadBlock(checked((int)header.TotalBoxSize));
		}

		/// <summary>
		/// Returns the exact bytes read from the file without re-serializing them.
		/// </summary>
		/// <param name="topData">Parent data; unused for opaque boxes.</param>
		/// <returns>The complete original box bytes.</returns>
		protected override ByteVector Render(ByteVector topData)
		{
			return _rawBytes;
		}
	}
}
