// Amplify Shader Editor - Visual Shader Editing Tool
// Copyright (c) Amplify Creations, Lda <info@amplify.pt>

// @diogo: transient preview-RT pool for QA #5 (pooled previews). See docs/preview-rt-pooling.md.
// Hands out RenderTextures at the global preview size/format, reused across a preview pass so only the
// peak concurrent live set is ever allocated. Grows on demand, never shrinks within a session, and
// flushes if the preview size/format preference changes. Callers must not Return an RT that is still
// aliased for display (e.g. a FunctionOutput RT referenced by a visible FunctionNode).

using System.Collections.Generic;
using UnityEngine;

namespace AmplifyShaderEditor
{
	public class ASEPreviewRTPool
	{
		private readonly List<RenderTexture> m_all = new List<RenderTexture>();
		private readonly Stack<RenderTexture> m_free = new Stack<RenderTexture>();
		private int m_size = -1;
		private RenderTextureFormat m_format = RenderTextureFormat.ARGBHalf;

		public int Allocated { get { return m_all.Count; } }
		public int InUse { get { return m_all.Count - m_free.Count; } }

		public RenderTexture Checkout()
		{
			EnsureCurrentFormat();

			RenderTexture rt;
			if ( m_free.Count > 0 )
			{
				rt = m_free.Pop();
			}
			else
			{
				rt = new RenderTexture( m_size, m_size, 0, m_format, RenderTextureReadWrite.Linear );
				rt.wrapMode = TextureWrapMode.Repeat;
				rt.Create();
				m_all.Add( rt );
			}
			return rt;
		}

		public void Return( RenderTexture rt )
		{
			if ( rt != null )
			{
				m_free.Push( rt );
			}
		}

		// Marks every allocated RT as free again ( end of a pass ) without releasing GPU memory.
		public void ReturnAll()
		{
			m_free.Clear();
			for ( int i = 0; i < m_all.Count; i++ )
			{
				m_free.Push( m_all[ i ] );
			}
		}

		public void Flush()
		{
			for ( int i = 0; i < m_all.Count; i++ )
			{
				if ( m_all[ i ] != null )
				{
					m_all[ i ].Release();
					Object.DestroyImmediate( m_all[ i ] );
				}
			}
			m_all.Clear();
			m_free.Clear();
		}

		private void EnsureCurrentFormat()
		{
			// @diogo: tracks the zoom-scaled size (QA #5d); a size/format change flushes so the next pass
			// re-allocates at the new size. Safe because all RTs are free between SF compute windows.
			if ( m_size != UIUtils.CurrentPreviewSize || m_format != Preferences.User.PreviewFormat )
			{
				Flush();
				m_size = UIUtils.CurrentPreviewSize;
				m_format = Preferences.User.PreviewFormat;
			}
		}
	}
}
