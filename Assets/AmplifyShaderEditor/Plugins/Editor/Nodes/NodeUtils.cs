// Amplify Shader Editor - Visual Shader Editing Tool
// Copyright (c) Amplify Creations, Lda <info@amplify.pt>

using UnityEngine;
using UnityEditor;

namespace AmplifyShaderEditor
{
	public class NodeUtils
	{
		private const float HelpBoxIconWidth = 36;        // icon column inside the box
		private const float ParamsWindowWidthSlack = 36;  // scroll view margins + scrollbar allowance

		private static GUIStyle m_helpBoxTextStyle = null;
		private static GUIContent m_tempTextContent = new GUIContent();

		public static float ParametersWindowWidth
		{
			get
			{
				return ( UIUtils.CurrentWindow != null && UIUtils.CurrentWindow.ParametersWindow != null ) ?
					UIUtils.CurrentWindow.ParametersWindow.TransformedArea.width : EditorGUIUtility.currentViewWidth;
			}
		}

		// Measures the height "text" needs when wrapped inside the node parameters panel. Word-wrapped
		// controls drawn in there must be given an explicit height; otherwise their reserved height
		// changes with scrollbar visibility, which can lock the scrollbar on even when content fits
		public static float MeasureTextHeight( string text, GUIStyle style, float occupiedWidth = 0 )
		{
			m_tempTextContent.text = text;
			return style.CalcHeight( m_tempTextContent, ParametersWindowWidth - ParamsWindowWidthSlack - occupiedWidth );
		}

		// Stable replacement for EditorGUILayout.HelpBox inside the node parameters scroll view ( see
		// MeasureTextHeight ); also draws the icon separately since including it in the content makes
		// CalcHeight undershoot
		public static void DrawHelpBox( string text, MessageType type )
		{
			if ( m_helpBoxTextStyle == null )
			{
				// Built from scratch instead of copying EditorStyles.helpBox; copies keep drawing
				// the box background through HiDPI scaled backgrounds, even when set to none
				m_helpBoxTextStyle = new GUIStyle();
				m_helpBoxTextStyle.fontSize = EditorStyles.helpBox.fontSize;
				m_helpBoxTextStyle.wordWrap = true;
				m_helpBoxTextStyle.padding = EditorStyles.helpBox.padding;
				m_helpBoxTextStyle.normal.textColor = EditorStyles.helpBox.normal.textColor;
			}

			float height = Mathf.Max( MeasureTextHeight( text, m_helpBoxTextStyle, HelpBoxIconWidth ), 40 );
			Rect rect = EditorGUILayout.GetControlRect( false, height );

			GUI.Label( rect, string.Empty, EditorStyles.helpBox );

			Texture icon = null;
			switch ( type )
			{
				case MessageType.Info: icon = EditorGUIUtility.IconContent( "console.infoicon" ).image; break;
				case MessageType.Warning: icon = EditorGUIUtility.IconContent( "console.warnicon" ).image; break;
				case MessageType.Error: icon = EditorGUIUtility.IconContent( "console.erroricon" ).image; break;
			}
			if ( icon != null )
			{
				Rect iconRect = new Rect( rect.x + 2, rect.y + 4, 32, 32 );
				GUI.DrawTexture( iconRect, icon, ScaleMode.ScaleToFit );
			}

			m_tempTextContent.text = text;
			Rect textRect = new Rect( rect.x + HelpBoxIconWidth, rect.y, rect.width - HelpBoxIconWidth, rect.height );
			GUI.Label( textRect, m_tempTextContent, m_helpBoxTextStyle );
		}

		public delegate void DrawPropertySection();

		public static void DrawPropertyGroup( string sectionName, DrawPropertySection DrawSection )
		{
			Color cachedColor = GUI.color;
			GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
			EditorGUILayout.BeginHorizontal( UIUtils.MenuItemToolbarStyle );
			GUI.color = cachedColor;

			GUILayout.Label( sectionName, UIUtils.MenuItemToggleStyle );

			EditorGUILayout.EndHorizontal();


			cachedColor = GUI.color;
			GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
			EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
			GUI.color = cachedColor;
			DrawSection();
			EditorGUILayout.Separator();
			EditorGUILayout.EndVertical();
		}


		public static void DrawNestedPropertyGroup( ref bool foldoutValue, string sectionName, DrawPropertySection DrawSection, int horizontalSpacing = 15 )
		{
			GUILayout.BeginHorizontal();
			{
				GUILayout.Space( horizontalSpacing );
				EditorGUILayout.BeginVertical( EditorStyles.helpBox );
				{
					Color cachedColor = GUI.color;
					GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
					EditorGUILayout.BeginHorizontal();
					{
						GUI.color = cachedColor;
						bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
						if( Event.current.button == Constants.FoldoutMouseId )
						{
							foldoutValue = value;
						}
					}
					EditorGUILayout.EndHorizontal();
					EditorGUI.indentLevel--;
					if( foldoutValue )
					{
						cachedColor = GUI.color;
						GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
						{
							EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
							{
								GUI.color = cachedColor;
								DrawSection();
							}
							EditorGUILayout.EndVertical();
							EditorGUILayout.Separator();
						}
					}
					EditorGUI.indentLevel++;
				}
				EditorGUILayout.EndVertical();
			}
			GUILayout.EndHorizontal();
		}

		public static void DrawNestedPropertyGroup( ref bool foldoutValue, Rect rect, string sectionName, DrawPropertySection DrawSection, int horizontalSpacing = 15 )
		{
			var box = rect;
			box.height -= 2;
			GUI.Label( box, string.Empty, EditorStyles.helpBox );

			var tog = rect;
			tog.y -= ( tog.height - ( EditorGUIUtility.singleLineHeight + 5 ) ) * 0.5f;
			tog.xMin += 2;
			tog.xMax -= 2;
			tog.yMin += 2;
			bool value = GUI.Toggle( tog, foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
			if( Event.current.button == Constants.FoldoutMouseId )
			{
				foldoutValue = value;
			}

			if( foldoutValue )
			{
				DrawSection();
			}
		}


		public static void DrawNestedPropertyGroup( ref bool foldoutValue, string sectionName, DrawPropertySection DrawSection, DrawPropertySection HeaderSection )
		{
			GUILayout.BeginHorizontal();
			{
				GUILayout.Space( 15 );
				EditorGUILayout.BeginVertical( EditorStyles.helpBox );
				Color cachedColor = GUI.color;
				GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
				EditorGUILayout.BeginHorizontal();
				GUI.color = cachedColor;

				bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
				if( Event.current.button == Constants.FoldoutMouseId )
				{
					foldoutValue = value;
				}
				HeaderSection();
				EditorGUILayout.EndHorizontal();
				EditorGUI.indentLevel--;
				if( foldoutValue )
				{
					cachedColor = GUI.color;
					GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
					EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
					GUI.color = cachedColor;
					DrawSection();
					EditorGUILayout.EndVertical();
					EditorGUILayout.Separator();
				}
				EditorGUI.indentLevel++;
				EditorGUILayout.EndVertical();
			}
			GUILayout.EndHorizontal();
		}

		public static void DrawNestedPropertyGroup( UndoParentNode owner, ref bool foldoutValue, ref bool enabledValue, string sectionName, DrawPropertySection DrawSection )
		{
			GUILayout.BeginHorizontal();
			{
				GUILayout.Space( 15 );
				EditorGUILayout.BeginVertical( EditorStyles.helpBox );
				Color cachedColor = GUI.color;
				GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
				EditorGUILayout.BeginHorizontal();
				GUI.color = cachedColor;

				bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
				if( Event.current.button == Constants.FoldoutMouseId )
				{
					foldoutValue = value;
				}
				
				value = ( (object)owner != null ) ? owner.GUILayoutToggle( enabledValue, string.Empty,UIUtils.MenuItemEnableStyle, GUILayout.Width( 16 ) ) :
										GUILayout.Toggle( enabledValue, string.Empty, UIUtils.MenuItemEnableStyle, GUILayout.Width( 16 ) );
				if( Event.current.button == Constants.FoldoutMouseId )
				{
					enabledValue = value;
				}
				

				EditorGUILayout.EndHorizontal();
				EditorGUI.indentLevel--;
				if( foldoutValue )
				{
					cachedColor = GUI.color;
					GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
					EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
					GUI.color = cachedColor;
					DrawSection();
					EditorGUILayout.EndVertical();
					EditorGUILayout.Separator();
				}
				EditorGUI.indentLevel++;
				EditorGUILayout.EndVertical();
			}
			GUILayout.EndHorizontal();
		}


		public static void DrawPropertyGroup( ref bool foldoutValue, string sectionName, DrawPropertySection DrawSection )
		{
			Color cachedColor = GUI.color;
			GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
			EditorGUILayout.BeginHorizontal( UIUtils.MenuItemToolbarStyle );
			GUI.color = cachedColor;

			bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
			if( Event.current.button == Constants.FoldoutMouseId )
			{
				foldoutValue = value;
			}
			EditorGUILayout.EndHorizontal();

			if( foldoutValue )
			{
				cachedColor = GUI.color;
				GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
				EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
				{
					GUI.color = cachedColor;
					EditorGUI.indentLevel++;
					DrawSection();
					EditorGUI.indentLevel--;
					EditorGUILayout.Separator();
				}
				EditorGUILayout.EndVertical();
			}
		}

		public static void DrawPropertyGroup( ref bool foldoutValue, string sectionName, DrawPropertySection DrawSection, DrawPropertySection HeaderSection )
		{
			Color cachedColor = GUI.color;
			GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
			EditorGUILayout.BeginHorizontal( UIUtils.MenuItemToolbarStyle );
			GUI.color = cachedColor;

			bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle );
			if( Event.current.button == Constants.FoldoutMouseId )
			{
				foldoutValue = value;
			}
			HeaderSection();
			EditorGUILayout.EndHorizontal();

			if( foldoutValue )
			{
				cachedColor = GUI.color;
				GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
				EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
				{
					GUI.color = cachedColor;
					EditorGUI.indentLevel++;
					DrawSection();
					EditorGUI.indentLevel--;
					EditorGUILayout.Separator();
				}
				EditorGUILayout.EndVertical();
			}
		}


		public static bool DrawPropertyGroup( UndoParentNode owner, ref bool foldoutValue, ref bool enabledValue, string sectionName, DrawPropertySection DrawSection )
		{
			bool enableChanged = false;
			Color cachedColor = GUI.color;
			GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, 0.5f );
			EditorGUILayout.BeginHorizontal( UIUtils.MenuItemToolbarStyle );
			GUI.color = cachedColor;
			bool value = GUILayout.Toggle( foldoutValue, sectionName, UIUtils.MenuItemToggleStyle, GUILayout.ExpandWidth( true ) );
			if( Event.current.button == Constants.FoldoutMouseId )
			{
				foldoutValue = value;
			}
			EditorGUI.BeginChangeCheck();
			value = ( (object)owner != null ) ? owner.EditorGUILayoutToggle( string.Empty, enabledValue, UIUtils.MenuItemEnableStyle, GUILayout.Width( 16 ) ) :
											EditorGUILayout.Toggle( string.Empty, enabledValue, UIUtils.MenuItemEnableStyle, GUILayout.Width( 16 ) );
			if( Event.current.button == Constants.FoldoutMouseId )
			{
				enabledValue = value;
			}
			if( EditorGUI.EndChangeCheck() )
			{
				enableChanged = true;
			}

			EditorGUILayout.EndHorizontal();

			if( foldoutValue )
			{
				cachedColor = GUI.color;
				GUI.color = new Color( cachedColor.r, cachedColor.g, cachedColor.b, ( EditorGUIUtility.isProSkin ? 0.5f : 0.25f ) );
				EditorGUILayout.BeginVertical( UIUtils.MenuItemBackgroundStyle );
				GUI.color = cachedColor;

				EditorGUILayout.Separator();
				EditorGUI.BeginDisabledGroup( !enabledValue );

				EditorGUI.indentLevel += 1;

				DrawSection();

				EditorGUI.indentLevel -= 1;
				EditorGUI.EndDisabledGroup();
				EditorGUILayout.Separator();
				EditorGUILayout.EndVertical();
			}

			return enableChanged;
		}
	}
}
