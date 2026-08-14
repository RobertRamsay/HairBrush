// Amplify Shader Editor - Visual Shader Editing Tool
// Copyright (c) Amplify Creations, Lda <info@amplify.pt>

using UnityEngine;
using System.Collections.Generic;
using UnityEditor;
using System;
using System.Text.RegularExpressions;

namespace AmplifyShaderEditor
{
	public class PaletteFilterData
	{
		public bool Visible;
		public bool HasCommunityData;
		public List<ContextMenuItem> Contents;
		public PaletteFilterData( bool visible )
		{
			Visible = visible;
			Contents = new List<ContextMenuItem>();
		}
	}

	public class PaletteParent : MenuParent
	{
		private const float ItemSize = 18;
		public delegate void OnPaletteNodeCreate( System.Type type, string name, AmplifyShaderFunction function );
		public event OnPaletteNodeCreate OnPaletteNodeCreateEvt;

		private string m_searchFilterStr = "Search";
		protected string m_searchFilterControl = "SHADERNAMETEXTFIELDCONTROLNAME";
		protected bool m_focusOnSearch = false;
		protected bool m_defaultCategoryVisible = false;

		//protected List<ContextMenuItem> m_allItems;
		protected List<ContextMenuItem> m_currentItems;
		protected Dictionary<string, PaletteFilterData> m_currentCategories;
		private bool m_forceUpdate = true;

		protected bool m_usingSearchFilter = false;
		protected string m_searchFilter = string.Empty;

		private float m_searchLabelSize = -1;
		private GUIStyle m_buttonStyle;
		private GUIStyle m_foldoutStyle;

		protected bool m_previousWindowIsFunction = false;

		protected int m_validButtonId = 0;
		protected int m_initialSeparatorAmount = 1;

		private Vector2 m_currScrollBarDims = new Vector2( 1, 1 );

		private const int NodesTabIndex = 0;
		private const int ReservedTabIndex = 1;
		private const int AutoBackupTabIndex = 2;
		private readonly string[] m_tabContents = { "Nodes", "Reserved", "Auto Backup" };
		private int m_selectedTab = NodesTabIndex;
		private Vector2 m_reservedScrollPos = Vector2.zero;
		// @diogo: reserved-name lists are cached and rebuilt only on Layout, so the IMGUI control count stays stable across the Layout/Repaint passes
		private bool m_reservedHasTemplate = false;
		private List<string> m_reservedTemplateNames = new List<string>();
		private List<string> m_reservedNodeNames = new List<string>();

		// Auto Backup tab state. The history is read from disk and cached ( the palette repaints often );
		// it refreshes on a short interval, on a key change and when forced ( after a manual backup ).
		private Vector2 m_autoBackupScrollPos = Vector2.zero;
		private int m_selectedBackupIndex = -1;
		private List<AutoBackupEntry> m_backupHistory = new List<AutoBackupEntry>();
		private string m_backupHistoryKey = null;
		private int m_lastBackupRevision = -1;
		private double m_nextBackupListRefresh = 0;

		// @diogo: in-place rename state for backup rows ( F2 / right-click ). Transitions are deferred to the
		// @diogo: Layout pass so a row's control count stays identical between the Layout and Repaint passes.
		private string m_renamingBackupPath = null;
		private string m_renameBackupText = string.Empty;
		private bool m_focusRenameField = false;
		private string m_pendingBeginRenamePath = null;
		private bool m_pendingRenameCommit = false;
		private bool m_pendingRenameCancel = false;
		private const string BackupRenameControlName = "ASEBackupRename";

		// True only when the docked palette is open on the Auto Backup tab. Lets the editor window drive a
		// light periodic repaint ( for the relative timestamps ) without repainting the rest of the time.
		public bool IsAutoBackupTabActive { get { return IsMaximized && SupportsReservedTab && m_selectedTab == AutoBackupTabIndex; } }

		// Only the docked palette shows the Nodes/Reserved tabs; node-creation popups keep the plain list
		protected virtual bool SupportsReservedTab { get { return false; } }

		public PaletteParent( AmplifyShaderEditorWindow parentWindow, float x, float y, float width, float height, string name, MenuAnchor anchor = MenuAnchor.NONE, MenuAutoSize autoSize = MenuAutoSize.NONE ) : base( parentWindow, x, y, width, height, name, anchor, autoSize )
		{
			m_searchFilter = string.Empty;
			m_currentCategories = new Dictionary<string, PaletteFilterData>();
			//m_allItems = items;
			m_currentItems = new List<ContextMenuItem>();
			m_selectedTab = EditorVariablesManager.NodePaletteSelectedTab.Value;
		}

		public virtual void OnEnterPressed( int index = 0 ) { }
		public virtual void OnEscapePressed() { }

		public void FireNodeCreateEvent( System.Type type, string name, AmplifyShaderFunction function )
		{
			OnPaletteNodeCreateEvt( type, name, function );
		}

		public override void Draw( Rect parentPosition, Vector2 mousePosition, int mouseButtonId, bool hasKeyboadFocus )
		{
			base.Draw( parentPosition, mousePosition, mouseButtonId, hasKeyboadFocus );
			if( m_previousWindowIsFunction != ParentWindow.IsShaderFunctionWindow )
			{
				m_forceUpdate = true;
			}

			m_previousWindowIsFunction = ParentWindow.IsShaderFunctionWindow;

			List<ContextMenuItem> allItems = ParentWindow.ContextMenuInstance.MenuItems;

			if ( Event.current.type == EventType.Layout && m_forceUpdate )
			{
				m_forceUpdate = false;

				//m_currentItems.Clear();
				m_currentCategories.Clear();

				if( m_usingSearchFilter )
				{
					for( int i = 0; i < allItems.Count; i++ )
					{
						//m_currentItems.Add( allItems[ i ] );
						if( !m_currentCategories.ContainsKey( allItems[ i ].Category ) )
						{
							m_currentCategories.Add( allItems[ i ].Category, new PaletteFilterData( m_defaultCategoryVisible ) );
							//m_currentCategories[ allItems[ i ].Category ].HasCommunityData = allItems[ i ].NodeAttributes.FromCommunity || m_currentCategories[ allItems[ i ].Category ].HasCommunityData;
						}
						m_currentCategories[ allItems[ i ].Category ].Contents.Add( allItems[ i ] );
					}
				}
				else
				{
					for( int i = 0; i < allItems.Count; i++ )
					{
						var searchList = m_searchFilter.Trim( ' ' ).ToLower().Split(' ');

						int matchesFound = 0;
						for( int k = 0; k < searchList.Length; k++ )
						{
							MatchCollection wordmatch = Regex.Matches( allItems[ i ].Tags, "\\b"+searchList[ k ] );
							if( wordmatch.Count > 0 )
								matchesFound++;
							else
								break;
						}

						if( searchList.Length == matchesFound )
						{
							//m_currentItems.Add( allItems[ i ] );
							if( !m_currentCategories.ContainsKey( allItems[ i ].Category ) )
							{
								m_currentCategories.Add( allItems[ i ].Category, new PaletteFilterData( m_defaultCategoryVisible ) );
								//m_currentCategories[ allItems[ i ].Category ].HasCommunityData = allItems[ i ].NodeAttributes.FromCommunity || m_currentCategories[ allItems[ i ].Category ].HasCommunityData;
							}
							m_currentCategories[ allItems[ i ].Category ].Contents.Add( allItems[ i ] );
						}
					}
				}
				var categoryEnumerator = m_currentCategories.GetEnumerator();
				while( categoryEnumerator.MoveNext() )
				{
					categoryEnumerator.Current.Value.Contents.Sort( ( x, y ) => x.CompareTo( y, m_usingSearchFilter ) );
				}

				//sort current list respecting categories
				m_currentItems.Clear();
				foreach( var item in m_currentCategories )
				{
					for( int i = 0; i < item.Value.Contents.Count; i++ )
					{
						m_currentItems.Add( item.Value.Contents[ i ] );
					}
				}
			}

			if( m_searchLabelSize < 0 )
			{
				m_searchLabelSize = GUI.skin.label.CalcSize( new GUIContent( m_searchFilterStr ) ).x;
			}

			if( m_foldoutStyle == null )
			{
				m_foldoutStyle = new GUIStyle( GUI.skin.GetStyle( "foldout" ) );
				m_foldoutStyle.fontStyle = FontStyle.Bold;
			}

			if( m_buttonStyle == null )
			{
				m_buttonStyle = UIUtils.Label;
			}

			Event currenEvent = Event.current;

			GUILayout.BeginArea( m_transformedArea, m_content, m_style );
			{
				for( int i = 0; i < m_initialSeparatorAmount; i++ )
				{
					EditorGUILayout.Separator();
				}

				if( SupportsReservedTab )
				{
					int selectedTab = GUILayout.Toolbar( m_selectedTab, m_tabContents );
					if ( selectedTab != m_selectedTab )
					{
						m_selectedTab = selectedTab;
						EditorVariablesManager.NodePaletteSelectedTab.Value = selectedTab;
					}
					EditorGUILayout.Separator();

					if( m_selectedTab == ReservedTabIndex )
					{
						DrawReservedNames();
						GUILayout.EndArea();
						return;
					}

					if( m_selectedTab == AutoBackupTabIndex )
					{
						DrawAutoBackup();
						GUILayout.EndArea();
						return;
					}
				}

				if( currenEvent.type == EventType.KeyDown )
				{
					KeyCode key = currenEvent.keyCode;
					//if ( key == KeyCode.Return || key == KeyCode.KeypadEnter )
					//	OnEnterPressed();

					if( ( currenEvent.keyCode == KeyCode.KeypadEnter || currenEvent.keyCode == KeyCode.Return ) && currenEvent.type == EventType.KeyDown )
					{
						int index = m_currentItems.FindIndex( x => GUI.GetNameOfFocusedControl().Equals( x.ItemUIContent.text + m_resizable ) );
						if( index > -1 )
							OnEnterPressed( index );
						else
							OnEnterPressed();
					}

					if( key == KeyCode.Escape )
						OnEscapePressed();

					if( m_isMouseInside || hasKeyboadFocus )
					{
						if( key == ShortcutsManager.ScrollUpKey )
						{
							m_currentScrollPos.y -= 10;
							if( m_currentScrollPos.y < 0 )
							{
								m_currentScrollPos.y = 0;
							}
							currenEvent.Use();
						}

						if( key == ShortcutsManager.ScrollDownKey )
						{
							m_currentScrollPos.y += 10;
							currenEvent.Use();
						}
					}

				}

				float width = EditorGUIUtility.labelWidth;
				EditorGUIUtility.labelWidth = m_searchLabelSize;
				EditorGUI.BeginChangeCheck();
				{
					var controlName = m_searchFilterControl + m_resizable;

					EditorGUILayout.BeginHorizontal();
					{
						// @diogo: split between Label and TextField to ensure FocusTextInControl is reliable
						Rect labelRect = EditorGUI.IndentedRect( EditorGUILayout.GetControlRect() );
						Rect valueRect = EditorGUI.PrefixLabel( labelRect, GUIContent.none );

						// @diogo: actual editable field
						GUI.SetNextControlName( controlName );
						m_searchFilter = EditorGUI.TextField( valueRect, m_searchFilter );

						// @diogo: temporary "transparent" hint
						{
							bool hasFocus = GUI.GetNameOfFocusedControl() == "randomName";
							if ( string.IsNullOrEmpty( m_searchFilter ) && Event.current.type == EventType.Repaint )
							{
								// Placeholder style (dim text)
								var hintStyle = new GUIStyle( EditorStyles.label )
								{
									alignment = TextAnchor.MiddleLeft,
									clipping = TextClipping.Clip,
									normal = { textColor = EditorGUIUtility.isProSkin ? new Color( .55f, .55f, .55f, 1 ) : new Color( .45f, .45f, .45f, 1 ) }
								};
								Rect hintRect = new Rect( valueRect.x + 3, valueRect.y, valueRect.width - 6, valueRect.height );
								GUI.Label( hintRect, "Search for nodes", hintStyle );
							}
						}
					}
					EditorGUILayout.EndHorizontal();

					if ( m_focusOnSearch && Event.current.type == EventType.Repaint )
					{
						m_focusOnSearch = false;
						EditorGUI.FocusTextInControl( controlName );
					}
				}
				if ( EditorGUI.EndChangeCheck() )
				{
					m_forceUpdate = true;
				}

				EditorGUIUtility.labelWidth = width;
				bool usingSearchFilter = ( m_searchFilter.Length == 0 );
				m_currScrollBarDims.x = m_transformedArea.width;
				m_currScrollBarDims.y = m_transformedArea.height - 2 - 16 - 2 - 7 * m_initialSeparatorAmount - 2;
				m_currentScrollPos = EditorGUILayout.BeginScrollView( m_currentScrollPos/*, GUILayout.Width( 242 ), GUILayout.Height( 250 - 2 - 16 - 2 - 7 - 2) */);
				{
					string watching = string.Empty;

					// unselect the main search field so it can focus list elements next
					if( ( currenEvent.keyCode == KeyCode.DownArrow || currenEvent.keyCode == KeyCode.UpArrow ) && m_searchFilter.Length > 0 )
					{
						if( GUI.GetNameOfFocusedControl().Equals( m_searchFilterControl + m_resizable ) )
						{
							EditorGUI.FocusTextInControl( null );
						}
					}

					if( currenEvent.keyCode == KeyCode.DownArrow && currenEvent.type == EventType.KeyDown )
					{
						currenEvent.Use();

						int nextIndex = m_currentItems.FindIndex( x => GUI.GetNameOfFocusedControl().Equals( x.ItemUIContent.text + m_resizable ) ) + 1;
						if( nextIndex == m_currentItems.Count )
							nextIndex = 0;

						watching = m_currentItems[ nextIndex ].ItemUIContent.text + m_resizable;
						GUI.FocusControl( watching );

					}

					if( currenEvent.keyCode == KeyCode.UpArrow && currenEvent.type == EventType.KeyDown )
					{
						currenEvent.Use();

						int nextIndex = m_currentItems.FindIndex( x => GUI.GetNameOfFocusedControl().Equals( x.ItemUIContent.text + m_resizable ) ) - 1;
						if( nextIndex < 0 )
							nextIndex = m_currentItems.Count - 1;

						watching = m_currentItems[ nextIndex ].ItemUIContent.text + m_resizable;
						GUI.FocusControl( watching );
					}

					if( currenEvent.keyCode == KeyCode.Tab )
					{
						ContextMenuItem item = m_currentItems.Find( x => GUI.GetNameOfFocusedControl().Equals( x.ItemUIContent.text + m_resizable ) );
						if( item != null )
						{
							watching = item.ItemUIContent.text + m_resizable;
						}
					}

					float currPos = 0;
					var enumerator = m_currentCategories.GetEnumerator();

					float cache = EditorGUIUtility.labelWidth;
					while( enumerator.MoveNext() )
					{
						var current = enumerator.Current;
						bool visible = GUILayout.Toggle( current.Value.Visible, current.Key, m_foldoutStyle );
						if( m_validButtonId == mouseButtonId )
						{
							current.Value.Visible = visible;
						}

						currPos += ItemSize;
						if( m_searchFilter.Length > 0 || current.Value.Visible )
						{
							for( int i = 0; i < current.Value.Contents.Count; i++ )
							{
								//if ( !IsItemVisible( currPos ) )
								//{
								//	// Invisible
								//	GUILayout.Space( ItemSize );
								//}
								//else
								{
									currPos += ItemSize;
									// Visible
									EditorGUILayout.BeginHorizontal();
									GUILayout.Space( 16 );
									//if ( m_isMouseInside )
									//{
									//	//GUI.SetNextControlName( current.Value.Contents[ i ].ItemUIContent.text );
									//	if ( CheckButton( current.Value.Contents[ i ].ItemUIContent, m_buttonStyle, mouseButtonId ) )
									//	{
									//		int controlID = GUIUtility.GetControlID( FocusType.Passive );
									//		GUIUtility.hotControl = controlID;
									//		OnPaletteNodeCreateEvt( current.Value.Contents[ i ].NodeType, current.Value.Contents[ i ].Name, current.Value.Contents[ i ].Function );
									//	}
									//}
									//else
									{
										Rect thisRect = EditorGUILayout.GetControlRect( false, 16f, EditorStyles.label );
										//if ( m_resizable )
										{
											if( GUI.RepeatButton( thisRect, string.Empty, EditorStyles.label ) )
											{
												int controlID = GUIUtility.GetControlID( FocusType.Passive );
												GUIUtility.hotControl = controlID;
												OnPaletteNodeCreateEvt( current.Value.Contents[ i ].NodeType, current.Value.Contents[ i ].Name, current.Value.Contents[ i ].Function );
												//unfocus to make it focus the next text field correctly
												GUI.FocusControl( null );
											}
										}
										GUI.SetNextControlName( current.Value.Contents[ i ].ItemUIContent.text + m_resizable );
										//EditorGUI.SelectableLabel( thisRect, current.Value.Contents[ i ].ItemUIContent.text, EditorStyles.label );
										//float cache = EditorGUIUtility.labelWidth;
										EditorGUIUtility.labelWidth = thisRect.width;
										EditorGUI.Toggle( thisRect, current.Value.Contents[ i ].ItemUIContent.text, false, EditorStyles.label );
										EditorGUIUtility.labelWidth = cache;
										if( watching == current.Value.Contents[ i ].ItemUIContent.text + m_resizable )
										{
											bool boundBottom = currPos - m_currentScrollPos.y > m_currScrollBarDims.y;
											bool boundTop = currPos - m_currentScrollPos.y - 4 <= 0;

											if( boundBottom )
												m_currentScrollPos.y = currPos - m_currScrollBarDims.y + 2;
											else if( boundTop )
												m_currentScrollPos.y = currPos - 18;
											//else if ( boundBottom && !downDirection )
											//	m_currentScrollPos.y = currPos - m_currScrollBarDims.y + 2;
											//else if ( boundTop && downDirection )
											//	m_currentScrollPos.y = currPos - 18;
										}
									}
									EditorGUILayout.EndHorizontal();
								}
								//currPos += ItemSize;
							}
						}
					}
					EditorGUIUtility.labelWidth = cache;
				}
				EditorGUILayout.EndScrollView();
			}
			GUILayout.EndArea();

		}

		private void DrawReservedNames()
		{
			// @diogo: rebuild the cached lists only on Layout; reservation state can change mid-frame as nodes register uniform names
			if( Event.current.type == EventType.Layout )
			{
				RefreshReservedNames();
			}

			m_reservedScrollPos = EditorGUILayout.BeginScrollView( m_reservedScrollPos );
			{
				// Template-reserved properties
				EditorGUILayout.LabelField( "Template", EditorStyles.boldLabel );

				if( m_reservedHasTemplate )
				{
					for( int i = 0; i < m_reservedTemplateNames.Count; i++ )
					{
						EditorGUILayout.LabelField( "    " + m_reservedTemplateNames[ i ] );
					}
					if( m_reservedTemplateNames.Count == 0 )
					{
						EditorGUILayout.LabelField( "    <none>" );
					}
				}
				else
				{
					EditorGUILayout.LabelField( "    <no template>" );
				}

				EditorGUILayout.Separator();

				// Node-owned uniform names
				EditorGUILayout.LabelField( "Nodes", EditorStyles.boldLabel );

				for( int i = 0; i < m_reservedNodeNames.Count; i++ )
				{
					EditorGUILayout.LabelField( "    " + m_reservedNodeNames[ i ] );
				}
				if( m_reservedNodeNames.Count == 0 )
				{
					EditorGUILayout.LabelField( "    <none>" );
				}
			}
			EditorGUILayout.EndScrollView();
		}

		// @diogo: collects the reserved template properties and node-owned uniform names, sorted alphanumerically
		private void RefreshReservedNames()
		{
			m_reservedTemplateNames.Clear();
			m_reservedNodeNames.Clear();

			TemplateMultiPassMasterNode templateMaster = ( ParentWindow.CurrentGraph != null ) ? ParentWindow.CurrentGraph.CurrentMasterNode as TemplateMultiPassMasterNode : null;
			m_reservedHasTemplate = templateMaster != null && templateMaster.CurrentTemplate != null;
			if( m_reservedHasTemplate )
			{
				List<TemplateShaderPropertyData> properties = templateMaster.CurrentTemplate.AvailableShaderProperties;
				for( int i = 0; i < properties.Count; i++ )
				{
					// Unused properties are not reserved, so they are not listed here
					if ( !properties[ i ].NameInUse )
					{
						continue;
					}
					m_reservedTemplateNames.Add( properties[ i ].PropertyName );
				}
				m_reservedTemplateNames.Sort( StringComparer.OrdinalIgnoreCase );
			}

			DuplicatePreventionBuffer buffer = ParentWindow.DuplicatePrevBufferInstance;
			if( buffer != null )
			{
				foreach( var pair in buffer.AvailableUniformNames )
				{
					if( pair.Value < 0 )
						continue;

					ParentNode owner = ( ParentWindow.CurrentGraph != null ) ? ParentWindow.CurrentGraph.GetNode( pair.Value ) : null;
					// Names owned by a master node are template reservations, already listed under Template
					if( owner == null || owner is MasterNode )
						continue;

					m_reservedNodeNames.Add( pair.Key + "  (" + owner.Attributes.Name + ")" );
				}
				m_reservedNodeNames.Sort( StringComparer.OrdinalIgnoreCase );
			}
		}

		// Auto Backup tab: history for the open graph, newest first. Single click selects ( highlight ),
		// double click asks for confirmation and replaces the graph with the chosen backup.
		private void DrawAutoBackup()
		{
			string key = ParentWindow.AutoBackupKey;
			Event evt = Event.current;

			if ( evt.type == EventType.Layout )
			{
				ApplyPendingBackupRenameTransitions();
				RefreshBackupHistory( key, false );
				// @diogo: the renamed row is tracked by path; if it vanished ( pruned / graph key change ) end the
				// rename so its clear-state logic isn't stranded on a row that no longer draws.
				if ( !string.IsNullOrEmpty( m_renamingBackupPath ) && IndexOfBackupPath( m_renamingBackupPath ) < 0 )
				{
					EndBackupRename();
				}
			}

			EditorGUILayout.LabelField( ParentWindow.AutoBackupDisplayName, EditorStyles.boldLabel );

			if ( !Preferences.User.AutoBackupEnabled )
			{
				EditorGUILayout.HelpBox( "Auto Backup is disabled. Enable it in Preferences > Amplify Shader Editor.", MessageType.Info );
			}

			EditorGUILayout.BeginHorizontal();
			if ( GUILayout.Button( "Backup Now" ) )
			{
				ParentWindow.PerformAutoBackup( true );
				// Force a re-list on the next Layout pass rather than mutating the row count mid-frame.
				m_nextBackupListRefresh = 0;
				m_selectedBackupIndex = -1;
			}
			if ( GUILayout.Button( "Open Folder" ) )
			{
				AutoBackup.RevealFolder( key );
				DeselectBackup();
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Separator();

			if ( m_backupHistory.Count == 0 )
			{
				EditorGUILayout.LabelField( "No backups yet." );
			}
			else
			{
				EditorGUILayout.LabelField( m_backupHistory.Count + " backup(s) - double-click to restore", EditorStyles.miniLabel );
			}

			if ( evt.type == EventType.KeyDown && evt.keyCode == KeyCode.F2 && string.IsNullOrEmpty( m_renamingBackupPath )
				&& string.IsNullOrEmpty( m_pendingBeginRenamePath ) && m_selectedBackupIndex >= 0 && m_selectedBackupIndex < m_backupHistory.Count )
			{
				m_pendingBeginRenamePath = m_backupHistory[ m_selectedBackupIndex ].FilePath;
				evt.Use();
			}

			m_autoBackupScrollPos = EditorGUILayout.BeginScrollView( m_autoBackupScrollPos );
			{
				for ( int i = 0; i < m_backupHistory.Count; i++ )
				{
					AutoBackupEntry entry = m_backupHistory[ i ];
					Rect rowRect = EditorGUILayout.GetControlRect( false, 20f );
					bool renamingRow = !string.IsNullOrEmpty( m_renamingBackupPath ) && entry.FilePath == m_renamingBackupPath;

					if ( string.IsNullOrEmpty( m_renamingBackupPath ) && evt.type == EventType.MouseDown && rowRect.Contains( evt.mousePosition ) )
					{
						if ( evt.button == 1 )
						{
							m_selectedBackupIndex = i;
							ShowBackupContextMenu( i );
						}
						else if ( evt.button == 0 && evt.clickCount == 2 )
						{
							RequestBackupRestore( entry );
						}
						else if ( evt.button == 0 )
						{
							m_selectedBackupIndex = i;
						}
						evt.Use();
					}

					if ( i == m_selectedBackupIndex && evt.type == EventType.Repaint )
					{
						EditorGUI.DrawRect( rowRect, EditorGUIUtility.isProSkin ? new Color( 0.24f, 0.37f, 0.59f, 1f ) : new Color( 0.36f, 0.54f, 0.90f, 1f ) );
					}

					Rect nameRect = new Rect( rowRect.x + 4f, rowRect.y, Mathf.Max( 30f, rowRect.width * 0.55f ), rowRect.height );
					Rect timeRect = new Rect( nameRect.xMax + 4f, rowRect.y, Mathf.Max( 0f, rowRect.xMax - nameRect.xMax - 4f ), rowRect.height );

					if ( renamingRow )
					{
						// @diogo: handle commit/cancel keys before the field draws, so the text control can't swallow Return first.
						if ( evt.type == EventType.KeyDown )
						{
							bool fieldFocused = GUI.GetNameOfFocusedControl() == BackupRenameControlName;
							if ( fieldFocused && ( evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter ) )
							{
								m_pendingRenameCommit = true;
								evt.Use();
							}
							// @diogo: Escape always cancels, even if the field never gained focus, so a rename can't strand
							else if ( evt.keyCode == KeyCode.Escape )
							{
								m_pendingRenameCancel = true;
								evt.Use();
							}
						}
						// @diogo: a mouse press outside the edit field commits the pending name ( click-away to save )
						else if ( evt.type == EventType.MouseDown && !nameRect.Contains( evt.mousePosition ) )
						{
							m_pendingRenameCommit = true;
							evt.Use();
						}

						GUI.SetNextControlName( BackupRenameControlName );
						m_renameBackupText = EditorGUI.TextField( nameRect, m_renameBackupText );

						if ( m_focusRenameField )
						{
							EditorGUI.FocusTextInControl( BackupRenameControlName );
							if ( evt.type == EventType.Repaint )
							{
								m_focusRenameField = false;
							}
						}

						EditorGUI.LabelField( timeRect, RelativeTime( entry.Timestamp ), EditorStyles.miniLabel );
					}
					else if ( AutoBackup.IsDefaultName( entry.DisplayName, ParentWindow.AutoBackupDisplayName ) )
					{
						// @diogo: un-renamed backups keep the original date/time-only row
						EditorGUI.LabelField( rowRect, "  " + FormatTimestamp( entry.Timestamp ) );
					}
					else
					{
						EditorGUI.LabelField( nameRect, new GUIContent( entry.DisplayName, FormatTimestamp( entry.Timestamp ) ) );
						EditorGUI.LabelField( timeRect, RelativeTime( entry.Timestamp ), EditorStyles.miniLabel );
					}
				}
			}
			EditorGUILayout.EndScrollView();

			// @diogo: any click that isn't on an entry row ( rows Use() their own click ) clears the selection. Not
			// @diogo: consumed, so the underlying control/canvas still gets it. Skipped while renaming ( commit handles it ).
			if ( string.IsNullOrEmpty( m_renamingBackupPath ) && evt.type == EventType.MouseDown )
			{
				DeselectBackup();
			}

			// @diogo: rename edits are queued during input events but only applied on the next Layout pass;
			// @diogo: request that pass now so the commit/cancel/begin shows immediately instead of on the next click.
			if ( m_pendingRenameCommit || m_pendingRenameCancel || !string.IsNullOrEmpty( m_pendingBeginRenamePath ) )
			{
				ParentWindow.Repaint();
			}
		}

		private void RefreshBackupHistory( string key, bool force )
		{
			double now = EditorApplication.timeSinceStartup;
			if ( !force && key == m_backupHistoryKey && AutoBackup.Revision == m_lastBackupRevision && now < m_nextBackupListRefresh )
			{
				return;
			}

			// @diogo: selection is tracked by row index, but a re-list can reorder/prune rows; capture the selected
			// @diogo: backup by path and re-resolve its index against the new list so the highlight and F2 stay correct.
			string selectedPath = ( m_selectedBackupIndex >= 0 && m_selectedBackupIndex < m_backupHistory.Count ) ? m_backupHistory[ m_selectedBackupIndex ].FilePath : null;

			m_backupHistory = AutoBackup.GetHistory( key );
			m_backupHistoryKey = key;
			m_lastBackupRevision = AutoBackup.Revision;
			m_nextBackupListRefresh = now + 2.0;

			m_selectedBackupIndex = string.IsNullOrEmpty( selectedPath ) ? -1 : IndexOfBackupPath( selectedPath );
		}

		private void RequestBackupRestore( AutoBackupEntry entry )
		{
			bool confirmed = EditorUtility.DisplayDialog(
				"Restore Auto Backup",
				"Replace the current graph with the backup from " + FormatTimestamp( entry.Timestamp ) + "?\n\nThe current graph will be lost ( this can be undone ).",
				"Restore", "Cancel" );
			if ( !confirmed )
			{
				return;
			}

			string snapshot = AutoBackup.LoadSnapshot( entry.FilePath );
			if ( string.IsNullOrEmpty( snapshot ) )
			{
				EditorUtility.DisplayDialog( "Restore Auto Backup", "The backup file could not be read.", "OK" );
				return;
			}

			ParentWindow.RequestBackupRestore( snapshot );
		}

		private void ShowBackupContextMenu( int index )
		{
			if ( index < 0 || index >= m_backupHistory.Count )
			{
				return;
			}
			AutoBackupEntry captured = m_backupHistory[ index ];
			GenericMenu menu = new GenericMenu();
			menu.AddItem( new GUIContent( "Restore" ), false, () => RequestBackupRestore( captured ) );
			menu.AddItem( new GUIContent( "Rename" ), false, () => { m_pendingBeginRenamePath = captured.FilePath; } );
			menu.ShowAsContext();
		}

		// @diogo: the backup list is re-listed off the GUI thread, so an index captured earlier can go stale;
		// @diogo: rename and the context menu track a backup by its file path and re-resolve the row here.
		private int IndexOfBackupPath( string path )
		{
			for ( int i = 0; i < m_backupHistory.Count; i++ )
			{
				if ( m_backupHistory[ i ].FilePath == path )
				{
					return i;
				}
			}
			return -1;
		}

		// @diogo: rename-state changes are applied here ( Layout only ) so the edited row's control count is
		// @diogo: identical across the Layout and Repaint passes of a frame.
		private void ApplyPendingBackupRenameTransitions()
		{
			if ( m_pendingRenameCommit || m_pendingRenameCancel )
			{
				ResolveActiveBackupRename();
			}

			if ( !string.IsNullOrEmpty( m_pendingBeginRenamePath ) )
			{
				int idx = IndexOfBackupPath( m_pendingBeginRenamePath );
				if ( idx >= 0 )
				{
					m_renamingBackupPath = m_pendingBeginRenamePath;
					m_selectedBackupIndex = idx;
					m_renameBackupText = m_backupHistory[ idx ].DisplayName;
					m_focusRenameField = true;
				}
				m_pendingBeginRenamePath = null;
			}
		}

		private void EndBackupRename()
		{
			m_renamingBackupPath = null;
			m_focusRenameField = false;
		}

		// @diogo: resolves an in-progress rename: commit the typed name, or honor a queued Escape-cancel. Shared by the
		// @diogo: Layout-pass flush and the window focus-loss hook, since the deferred flush can't run while unfocused.
		private void ResolveActiveBackupRename()
		{
			bool cancel = m_pendingRenameCancel;
			m_pendingRenameCommit = false;
			m_pendingRenameCancel = false;

			if ( string.IsNullOrEmpty( m_renamingBackupPath ) )
			{
				return;
			}

			if ( !cancel )
			{
				// @diogo: an empty name resets the backup to its default ( shader name ), so the row shows date/time again.
				string trimmed = m_renameBackupText != null ? m_renameBackupText.Trim() : string.Empty;
				string newLabel = trimmed.Length > 0 ? trimmed : ParentWindow.AutoBackupDisplayName;
				AutoBackup.Rename( m_renamingBackupPath, newLabel );
				m_nextBackupListRefresh = 0;
			}
			EndBackupRename();
		}

		// @diogo: clears the auto-backup row selection ( no entry highlighted ).
		private void DeselectBackup()
		{
			if ( m_selectedBackupIndex < 0 )
			{
				return;
			}
			m_selectedBackupIndex = -1;
			ParentWindow.Repaint();
		}

		// @diogo: the user moved away from the backup list ( clicked the canvas or another window ): commit/close any
		// @diogo: in-progress rename and clear the selection so no entry stays highlighted.
		public void CommitAndClearBackupSelection()
		{
			ResolveActiveBackupRename();
			DeselectBackup();
		}

		// @diogo: the editor window stops repainting while unfocused, so the on-GUI commit paths never run; resolve
		// @diogo: any active rename and clear the selection here ( the owning window forwards its OnLostFocus ).
		public override void OnLostFocus()
		{
			base.OnLostFocus();
			CommitAndClearBackupSelection();
		}

		private static string FormatTimestamp( DateTime time )
		{
			return time.ToString( "yyyy-MM-dd HH:mm:ss" ) + "    " + RelativeTime( time );
		}

		private static string RelativeTime( DateTime time )
		{
			TimeSpan span = DateTime.Now - time;
			if ( span.TotalSeconds < 60 )
			{
				return "(just now)";
			}
			if ( span.TotalMinutes < 60 )
			{
				return "(" + (int)span.TotalMinutes + " min ago)";
			}
			if ( span.TotalHours < 24 )
			{
				return "(" + (int)span.TotalHours + " h ago)";
			}
			return "(" + (int)span.TotalDays + " d ago)";
		}

		public void CheckCommunityNodes()
		{
			var enumerator = m_currentCategories.GetEnumerator();
			while( enumerator.MoveNext() )
			{
				var current = enumerator.Current;
				current.Value.HasCommunityData = false;
				int count = current.Value.Contents.Count;
				for( int i = 0; i < count; i++ )
				{
					if( current.Value.Contents[ i ].NodeAttributes.FromCommunity )
					{
						current.Value.HasCommunityData = true;
						break;
					}
				}
			}
		}

		public void DumpAvailableNodes( bool fromCommunity, string pathname )
		{
			string noTOCHeader = "__NOTOC__\n";
			string nodesHeader = "== Available Node Categories ==\n";
			string InitialCategoriesFormat = "[[#{0}|{0}]]<br>\n";
			string InitialCategories = string.Empty;
			string CurrentCategoryFormat = "\n== {0} ==\n\n";
			//string NodesFootFormat = "[[Unity Products:Amplify Shader Editor/{0} | Learn More]] -\n[[#Top|Back to Categories]]\n";
			string NodesFootFormatSep = "[[#Top|Back to Top]]\n----\n";
			string OverallFoot = "[[Category:Nodes]]";

			string NodeInfoBeginFormat = "<div class=\"nodecard\">\n";
			string nodeInfoBodyFormat = "{{| id=\"{2}\" class=\"wikitable\" |\n" +
				"|- \n" +
				"| <div>[[Unity Products:Amplify Shader Editor/{1}|<img class=\"responsive-img\" src=\"http://amplify.pt/Nodes/{0}.jpg\">]]</div>\n" +
				"<div>\n" +
				"{{| style=\"width: 100%; height: 150px;\"\n" +
				"|-\n" +
				"| [[Unity Products:Amplify Shader Editor/{1}|'''{2}''']]\n" +
				"|- style=\"vertical-align:top; height: 100%;\" |\n" +
				"|<p class=\"cardtext\">{3}</p>\n" +
				"|- style=\"text-align:right;\" |\n" +
				"|{4}[[Unity Products:Amplify Shader Editor/{1} | Learn More]]\n" +
				"|}}</div>\n" +
				"|}}\n";
			string NodeInfoEndFormat = "</div>\n";

			//string NodeInfoBeginFormat = "<span style=\"color:#c00;display:block;\">This page is under construction!</span>\n\n";
			//string nodeInfoBodyFormat = "<img style=\"float:left; margin-right:10px;\" src=\"http://amplify.pt/Nodes/{0}.jpg\">\n[[Unity Products:Amplify Shader Editor/{1}|'''{2}''']]\n\n{3}";
			//string NodeInfoEndFormat = "\n\n[[Unity_Products:Amplify_Shader_Editor/Nodes | Back to Node List ]]\n[[Category:Nodes]][[Category:{0}]]\n\n\n";

			//string NodeInfoBeginFormat = "{| cellpadding=\"10\"\n";
			//string nodeInfoBodyFormat = "|- style=\"vertical-align:top;\"\n| http://amplify.pt/Nodes/{0}.jpg\n| [[Unity Products:Amplify Shader Editor/{1} | <span style=\"font-size: 120%;\"><span id=\"{2}\"></span>'''{2}'''<span> ]] <br> {3}\n";
			//string NodeInfoEndFormat = "|}\n";

			string nodesInfo = string.Empty;
			BuildFullList( true );
			CheckCommunityNodes();
			var enumerator = m_currentCategories.GetEnumerator();
			while( enumerator.MoveNext() )
			{
				var current = enumerator.Current;
				if( fromCommunity && current.Value.HasCommunityData || !fromCommunity )
				{
					InitialCategories += string.Format( InitialCategoriesFormat, current.Key );
					nodesInfo += string.Format( CurrentCategoryFormat, current.Key );
					int count = current.Value.Contents.Count;
					for( int i = 0; i < count; i++ )
					{
						if( ( fromCommunity && current.Value.Contents[ i ].NodeAttributes.FromCommunity )
							|| !fromCommunity
							//|| ( !fromCommunity && !current.Value.Contents[ i ].NodeAttributes.FromCommunity )
							)
						{
							string nodeFullName = current.Value.Contents[ i ].Name;
							string pictureFilename = UIUtils.ReplaceInvalidStrings( nodeFullName );

							string pageFilename = UIUtils.RemoveWikiInvalidCharacters( pictureFilename );

							pictureFilename = UIUtils.RemoveInvalidCharacters( pictureFilename );

							string nodeDescription = current.Value.Contents[ i ].ItemUIContent.tooltip;
							string communityText = string.Empty;
							if( current.Value.Contents[ i ].NodeAttributes.FromCommunity )
								communityText = "<small class=\"cardauthor\">( originally by "+ current.Value.Contents[ i ].NodeAttributes.Community + " )</small> ";

							string nodeInfoBody = string.Format( nodeInfoBodyFormat, pictureFilename, pageFilename, nodeFullName, nodeDescription, communityText );
							//string nodeInfoFoot = string.Format( NodesFootFormat, pageFilename );

							nodesInfo += ( NodeInfoBeginFormat + nodeInfoBody + NodeInfoEndFormat );
							//nodesInfo += ( NodeInfoBeginFormat + nodeInfoBody + string.Format( NodeInfoEndFormat, current.Key ) );
							//if ( i != ( count - 1 ) )
							//{
							//	nodesInfo += NodesFootFormatSep;
							//}
						}
					}
					nodesInfo += NodesFootFormatSep;
				}
			}

			string finalText = noTOCHeader + nodesHeader + InitialCategories + nodesInfo + OverallFoot;

			if( !System.IO.Directory.Exists( pathname ) )
			{
				System.IO.Directory.CreateDirectory( pathname );
			}
			// Save file
			string nodesPathname = pathname + ( fromCommunity ? "AvailableNodesFromCommunity.txt" : "AvailableNodes.txt" );
			Debug.Log( " Creating nodes file at " + nodesPathname );
			IOUtils.SaveTextfileToDisk( finalText, nodesPathname, false );
			BuildFullList( false );
		}

		public virtual bool CheckButton( GUIContent content, GUIStyle style, int buttonId )
		{
			if( buttonId != m_validButtonId )
			{
				GUILayout.Label( content, style );
				return false;
			}

			return GUILayout.RepeatButton( content, style );
		}

		public void FillList( ref List<ContextMenuItem> list, bool forceAllItems )
		{
			List<ContextMenuItem> allList = forceAllItems ? ParentWindow.ContextMenuInstance.ItemFunctions : ParentWindow.ContextMenuInstance.MenuItems;

			list.Clear();
			int count = allList.Count;
			for( int i = 0; i < count; i++ )
			{
				list.Add( allList[ i ] );
			}
		}

		public Dictionary<string, PaletteFilterData> BuildFullList( bool forceAllNodes = false )
		{
			//Only need to build if search filter is active and list is set according to it
			if( m_searchFilter.Length > 0 || !m_isActive || m_currentCategories.Count == 0 )
			{
				m_currentItems.Clear();
				m_currentCategories.Clear();

				List<ContextMenuItem> allItems = forceAllNodes ? ParentWindow.ContextMenuInstance.ItemFunctions : ParentWindow.ContextMenuInstance.MenuItems;

				for( int i = 0; i < allItems.Count; i++ )
				{
					if( allItems[ i ].Name.IndexOf( m_searchFilter, StringComparison.InvariantCultureIgnoreCase ) >= 0 ||
							allItems[ i ].Category.IndexOf( m_searchFilter, StringComparison.InvariantCultureIgnoreCase ) >= 0
						)
					{
						m_currentItems.Add( allItems[ i ] );
						if( !m_currentCategories.ContainsKey( allItems[ i ].Category ) )
						{
							m_currentCategories.Add( allItems[ i ].Category, new PaletteFilterData( m_defaultCategoryVisible ) );
							//m_currentCategories[ allItems[ i ].Category ].HasCommunityData = allItems[ i ].NodeAttributes.FromCommunity || m_currentCategories[ allItems[ i ].Category ].HasCommunityData;
						}
						m_currentCategories[ allItems[ i ].Category ].Contents.Add( allItems[ i ] );
					}
				}

				var categoryEnumerator = m_currentCategories.GetEnumerator();
				while( categoryEnumerator.MoveNext() )
				{
					categoryEnumerator.Current.Value.Contents.Sort( ( x, y ) => x.CompareTo( y, false ) );
				}

				//mark to force update and take search filter into account
				m_forceUpdate = true;
			}
			return m_currentCategories;
		}

		private bool IsItemVisible( float currPos )
		{
			if( ( currPos < m_currentScrollPos.y && ( currPos + ItemSize ) < m_currentScrollPos.y ) ||
									( currPos > ( m_currentScrollPos.y + m_currScrollBarDims.y ) &&
									( currPos + ItemSize ) > ( m_currentScrollPos.y + m_currScrollBarDims.y ) ) )
			{
				return false;
			}
			return true;
		}

		public override void Destroy()
		{
			base.Destroy();

			//m_allItems = null;

			m_currentItems.Clear();
			m_currentItems = null;

			m_currentCategories.Clear();
			m_currentCategories = null;

			OnPaletteNodeCreateEvt = null;
			m_buttonStyle = null;
			m_foldoutStyle = null;
		}

		//public void Clear() {
		//	m_allItems.Clear();
		//	m_allItems = new List<ContextMenuItem>();
		//}

		public bool ForceUpdate { get { return m_forceUpdate; } set { m_forceUpdate = value; } }
	}
}
