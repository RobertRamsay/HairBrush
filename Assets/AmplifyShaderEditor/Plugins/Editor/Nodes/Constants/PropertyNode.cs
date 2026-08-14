// Amplify Shader Editor - Visual Shader Editing Tool
// Copyright (c) Amplify Creations, Lda <info@amplify.pt>

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace AmplifyShaderEditor
{
	public enum PropertyType
	{
		Constant = 0,
		Property,
		InstancedProperty,
		Global
	}

	public enum VariableMode
	{
		Create = 0,
		Fetch
	}

	[Serializable]
	public class PropertyAttributes
	{
		public string Name;
		public string Attribute;
		public bool HasDeprecatedValue;
		public string DeprecatedValue;
		public PropertyAttributes( string name, string attribute , string deprecated = null )
		{
			Name = name;
			Attribute = attribute;
			DeprecatedValue = deprecated;
			HasDeprecatedValue = deprecated != null;
		}
	}

	[Serializable]
	public class PropertyNode : ParentNode
	{
		private const string LongNameEnder = "... )";
		protected int m_longNameSize = 200;
		//private const string InstancedPropertyWarning = "Instanced Property option shouldn't be used on official SRP templates as all property variables are already declared as instanced inside a CBuffer.\nPlease consider changing to Property option.";
		private const string TooltipFormatter = "{0}\n\nName: {1}\nValue: {2}";
		private const string InvalidAttributeFormatter = "Attribute {0} not found on node {1}. Please click on this message to select node and review its attributes section";
		protected string GlobalTypeWarningText = "Global variables must be set via a C# script using the Shader.SetGlobal{0}(...) method.\nPlease note that setting a global variable will affect all shaders which are using it.";
		private const string PrecisionDisabledWarningText = "Precision cannot be changed because a matching internal property is already defined as global uniform in the template.";
		private const string HybridInstancedStr = "Hybrid Instanced";
		private const string AutoRegisterStr = "Auto-Register";
		private const string IgnoreVarDeclarationStr = "Variable Mode";
		private const string IsPropertyStr = "Is Property";
		private const string PropertyNameStr = "Property Name";
		private const string PropertyInspectorStr = "Name";
		protected const string EnumsStr = "Enums";
		protected const string CustomAttrStr = "Custom Attributes";
		protected const string HeaderAttrStr = "Headers";
		protected const string ParameterTypeStr = "Type";
		private const string PropertyTextfieldControlName = "PropertyName";
		private const string PropertyInspTextfieldControlName = "PropertyInspectorName";
		private const string OrderIndexStr = "Order Index";
		protected const double MaxTimestamp = 2;
		private const double MaxPropertyTimestamp = 2;
		private const double MaxGlobalFetchTimestamp = 2;
		protected readonly string[] LabelToolbarTitle = { "Material", "Default" };
		protected readonly string[] EnumModesStr = { "Create Enums", "Use Engine Enum Class" };
		protected readonly int[] EnumModeIntValues = { 0, 1 };
		private const string FetchToCreateDuplicatesMsg = "Reverting property name from '{0}' to '{1}' as it is registered to another property node.";
		private const string FetchToCreateOnDuplicateNodeMsg = "Setting new property name '{0}' as '{1}' is registered to another property node.";
		private const string HeaderId = "Header";
		private const string EnumId = "Enum";

		[SerializeField]
		protected PropertyType m_currentParameterType;

		[SerializeField]
		private PropertyType m_lastParameterType;

		[SerializeField]
		protected string m_propertyName = string.Empty;

		[SerializeField]
		protected string m_propertyInspectorName = string.Empty;

		[SerializeField]
		protected string m_precisionString;
		protected bool m_drawPrecisionUI = true;
		protected bool m_disablePrecisionUI = false;

		[SerializeField]
		private int m_orderIndex = -1;

		[SerializeField]
		protected VariableMode m_variableMode = VariableMode.Create;

		[SerializeField]
		protected bool m_autoGlobalName = true;

		[SerializeField]
		protected bool m_hybridInstanced = false;

		[SerializeField]
		protected bool m_autoRegister = false;

		[SerializeField]
		protected bool m_registerPropertyOnInstancing = true;

		[SerializeField]
		private List<string> m_enumNames = new List<string>();

		[SerializeField]
		private List<int> m_enumValues = new List<int>();

		[SerializeField]
		private int m_enumCount = 0;

		[SerializeField]
		private int m_enumModeInt = 0;

		[SerializeField]
		private int m_customAttrCount = 0;

		[SerializeField]
		private List<string> m_customAttr = new List<string>();

		// @diogo: opaque per-node payload owned by an external integration (see PropertyNodeExtensions).
		//         Retained verbatim so it round-trips even when no integration is registered.
		[SerializeField]
		private string m_extensionData = string.Empty;

		[SerializeField]
		private bool m_hasHeaders = false;

		[SerializeField]
		private List<string> m_headerAttributeValues = new List<string>();


		[SerializeField]
		private string m_enumClassName = string.Empty;

		private bool m_hasEnum = false;

		protected bool m_showTitleWhenNotEditing = true;

		private int m_orderIndexOffset = 0;

		protected bool m_drawAttributes = true;

		protected bool m_underscoredGlobal = false;
		protected bool m_globalDefaultBehavior = true;

		protected bool m_freeName;
		protected bool m_freeType;
		protected bool m_showVariableMode = false;
		protected bool m_propertyNameIsDirty;

		protected bool m_showAutoRegisterUI = true;

		protected bool m_showHybridInstancedUI = false;

		protected bool m_useVarSubtitle = false;

		protected bool m_propertyFromInspector;
		protected double m_propertyFromInspectorTimestamp;

		protected bool m_checkDuplicateProperty;
		protected double m_checkDuplicatePropertyTimestamp;

		protected double m_globalFetchTimestamp;

		protected bool m_delayedDirtyProperty;
		protected double m_delayedDirtyPropertyTimestamp;

		protected string m_defaultPropertyName;
		protected string m_oldName = string.Empty;

		private bool m_reRegisterName = false;
		protected bool m_allowPropertyDuplicates = false;
		//protected bool m_useCustomPrefix = false;
		protected string m_customPrefix = null;

		protected int m_propertyTab = 0;

		[SerializeField]
		private string m_uniqueName;

		// Property Attributes
		private const float ButtonLayoutWidth = 15;

		protected bool m_visibleAttribsFoldout;
		protected bool m_visibleEnumsFoldout;
		protected bool m_visibleCustomAttrFoldout;
		protected bool m_visibleHeaderAttrFoldout;
		protected List<PropertyAttributes> m_availableAttribs = new List<PropertyAttributes>();
		private string[] m_availableAttribsArr;

		[SerializeField]
		private bool[] m_selectedAttribsArr;

		[SerializeField]
		protected List<int> m_selectedAttribs = new List<int>();

		//Title editing
		protected bool m_isEditing;
		protected bool m_stopEditing;
		protected bool m_startEditing;
		protected double m_clickTime;
		protected double m_doubleClickTime = 0.3;
		private Rect m_titleClickArea;

		protected bool m_srpBatcherCompatible = false;
		protected bool m_excludeUniform = false;

		[SerializeField]
		private bool m_addGlobalToSRPBatcher = false;

		// @diogo: generic Object/Reference mode support; opted-in by Float/Int/Vector/Color nodes
		private readonly Color ReferenceHeaderColor = new Color( 0f, 0.5f, 0.585f, 1.0f );
		private const float ReferenceIconSize = 19;

		protected bool m_canBeReferenced = false;
		private Rect m_referenceIconPos;

		[SerializeField]
		private TexReferenceType m_propertyReferenceType = TexReferenceType.Object;

		[SerializeField]
		private int m_propertyReferenceNodeId = -1;

		private PropertyNode m_propertyReference = null;
		private string m_lastPropertyReferenceTitle = string.Empty;
		private string m_lastPropertyReferenceValue = string.Empty;
		private List<PropertyNode> m_propertyReferenceCandidates = new List<PropertyNode>();

		public PropertyNode() : base() { }
		public PropertyNode( int uniqueId, float x, float y, float width, float height ) : base( uniqueId, x, y, width, height ) { }


		protected override void CommonInit( int uniqueId )
		{
			base.CommonInit( uniqueId );
			m_textLabelWidth = 105;
			if( UIUtils.CurrentWindow != null && UIUtils.CurrentWindow.CurrentGraph != null )
				m_orderIndex = UIUtils.GetPropertyNodeAmount();
			m_currentParameterType = PropertyType.Constant;
			m_freeType = true;
			m_freeName = true;
			m_propertyNameIsDirty = true;
			m_customPrecision = true;
			m_availableAttribs.Add( new PropertyAttributes( "Hide in Inspector", "[HideInInspector]" ) );
			m_availableAttribs.Add( new PropertyAttributes( "HDR", "[HDR]" ) );
			m_availableAttribs.Add( new PropertyAttributes( "Gamma", "[Gamma]" ) );
			m_availableAttribs.Add( new PropertyAttributes( "Per Renderer Data", "[PerRendererData]" ) );
			m_availableAttribs.Add( new PropertyAttributes( "Header", "[Header]" ) );
		}

		public override void AfterCommonInit()
		{
			base.AfterCommonInit();

			if( PaddingTitleLeft == 0 && m_freeType )
			{
				PaddingTitleLeft = Constants.PropertyPickerWidth + Constants.IconsLeftRightMargin;
				if( PaddingTitleRight == 0 )
					PaddingTitleRight = Constants.PropertyPickerWidth + Constants.IconsLeftRightMargin;
			}

			m_hasLeftDropdown = m_freeType && !IsPropertyReference;
		}

		protected void BeginDelayedDirtyProperty()
		{
			m_delayedDirtyProperty = true;
			m_delayedDirtyPropertyTimestamp = EditorApplication.timeSinceStartup;
		}

		public void CheckDelayedDirtyProperty()
		{
			if( m_delayedDirtyProperty )
			{
				if( ( EditorApplication.timeSinceStartup - m_delayedDirtyPropertyTimestamp ) > MaxPropertyTimestamp )
				{
					m_delayedDirtyProperty = false;
					m_propertyNameIsDirty = true;
					m_sizeIsDirty = true;
				}
			}
		}

		public void BeginPropertyFromInspectorCheck()
		{
			m_propertyFromInspector = true;
			m_propertyFromInspectorTimestamp = EditorApplication.timeSinceStartup;
		}

		public virtual void CheckPropertyFromInspector( bool forceUpdate = false )
		{
			if( m_propertyFromInspector )
			{
				if( forceUpdate || ( EditorApplication.timeSinceStartup - m_propertyFromInspectorTimestamp ) > MaxTimestamp )
				{
					m_propertyFromInspector = false;
					bool autoGlobal = m_autoGlobalName || m_currentParameterType == PropertyType.Global;
					RegisterPropertyName( true, m_propertyInspectorName, autoGlobal, m_underscoredGlobal );
					m_propertyNameIsDirty = true;
				}
			}
		}

		public void CheckDuplicateProperty()
		{
			if( m_checkDuplicateProperty &&
				( EditorApplication.timeSinceStartup - m_checkDuplicatePropertyTimestamp ) > MaxTimestamp )
			{
				m_checkDuplicateProperty = false;
				m_propertyName = UIUtils.GeneratePropertyName( m_propertyName, PropertyType.Global, false );

				if( UIUtils.IsNumericName( m_propertyName ) )
				{
					UIUtils.ShowMessage( UniqueId, string.Format( "Invalid property name '{0}' as it cannot start with numbers. Reverting to last valid name '{1}'", m_propertyName, m_oldName ), MessageSeverity.Warning );
					m_propertyName = m_oldName;
					GUI.FocusControl( string.Empty );
					return;
				}

				if( !m_propertyName.Equals( m_oldName ) )
				{
					if( m_variableMode == VariableMode.Fetch )
					{
						m_oldName = m_propertyName;
						m_propertyNameIsDirty = true;
						m_reRegisterName = false;
						OnPropertyNameChanged();
					}
					else if( UIUtils.IsUniformNameAvailable( m_propertyName ) || m_allowPropertyDuplicates )
					{
						UIUtils.ReleaseUniformName( UniqueId, m_oldName );

						m_oldName = m_propertyName;
						m_propertyNameIsDirty = true;
						m_reRegisterName = false;
						UIUtils.RegisterUniformName( UniqueId, m_propertyName );
						OnPropertyNameChanged();
					}
					else
					{
						GUI.FocusControl( string.Empty );
						RegisterFirstAvailablePropertyName( true, true );
						UIUtils.ShowMessage( UniqueId, string.Format( "Property name '{0}' is already in use. Reverting to last valid name '{1}'", m_propertyName, m_oldName ) );
					}
				}
			}
		}

		protected override void OnUniqueIDAssigned()
		{
			if( m_variableMode == VariableMode.Create )
				RegisterFirstAvailablePropertyName( false );

			if( m_nodeAttribs != null )
				m_uniqueName = m_nodeAttribs.Name + UniqueId;

			UIUtils.RegisterRawPropertyNode( this );
		}

		public bool CheckLocalVariable( ref MasterNodeDataCollector dataCollector )
		{
			bool addToLocalValue = false;
			int count = 0;
			for( int i = 0; i < m_outputPorts.Count; i++ )
			{
				if( m_outputPorts[ i ].IsConnected )
				{
					if( m_outputPorts[ i ].ConnectionCount > 1 )
					{
						addToLocalValue = true;
						break;
					}
					count += 1;
					if( count > 1 )
					{
						addToLocalValue = true;
						break;
					}
				}
			}

			if( addToLocalValue )
			{
				ConfigureLocalVariable( ref dataCollector );
			}

			return addToLocalValue;
		}

		public virtual void ConfigureLocalVariable( ref MasterNodeDataCollector dataCollector ) { }
		public virtual void CopyDefaultsToMaterial() { }

		public override void SetupFromCastObject( UnityEngine.Object obj )
		{
			RegisterPropertyName( true, obj.name, true, m_underscoredGlobal );
		}

		public void ChangeParameterType( PropertyType parameterType )
		{

			bool wasReserving = ReservesUniformName;

			if( m_currentParameterType == PropertyType.Constant || m_currentParameterType == PropertyType.Global )
			{
				CopyDefaultsToMaterial();
			}

			if( parameterType == PropertyType.InstancedProperty )
			{
				//if( m_containerGraph.IsSRP )
				//{
				//	UIUtils.ShowMessage( InstancedPropertyWarning,MessageSeverity.Warning );
				//}

				UIUtils.CurrentWindow.OutsideGraph.AddInstancePropertyCount();
			}
			else if( m_currentParameterType == PropertyType.InstancedProperty )
			{
				UIUtils.CurrentWindow.OutsideGraph.RemoveInstancePropertyCount();
			}

			if( ( parameterType == PropertyType.Property || parameterType == PropertyType.InstancedProperty )
				&& m_currentParameterType != PropertyType.Property && m_currentParameterType != PropertyType.InstancedProperty )
			{
				UIUtils.RegisterPropertyNode( this );
			}

			if( ( parameterType != PropertyType.Property && parameterType != PropertyType.InstancedProperty )
				&& ( m_currentParameterType == PropertyType.Property || m_currentParameterType == PropertyType.InstancedProperty ) )
			{
				UIUtils.UnregisterPropertyNode( this );
			}

			m_currentParameterType = parameterType;
			if( parameterType == PropertyType.Constant )
			{
				CurrentVariableMode = VariableMode.Create;
			}

			// Reconcile registry ownership for the new type (C): flipping a constant into a reserving
			// type acquires (and dedups) a name now, instead of relying on a slot it used to hold while
			// constant; flipping back into a constant releases the slot.
			bool willReserve = ReservesUniformName;
			if( !wasReserving && willReserve )
			{
				if( UIUtils.IsUniformNameAvailable( m_propertyName ) )
					UIUtils.RegisterUniformName( UniqueId, m_propertyName );
				else
					RegisterFirstAvailablePropertyName( false );
			}
			else if( wasReserving && !willReserve )
			{
				if( UIUtils.CheckUniformNameOwner( m_propertyName ) == UniqueId )
					UIUtils.ReleaseUniformName( UniqueId, m_propertyName );
			}
		}

		public bool IsPropertyReference { get { return m_canBeReferenced && m_propertyReferenceType == TexReferenceType.Instance; } }
		public override int ReferencedNodeId { get { return IsPropertyReference ? m_propertyReferenceNodeId : base.ReferencedNodeId; } }
		public bool CanBeReferenced { get { return m_canBeReferenced; } }

		// Single rule for "does this node own a slot in the uniform-name dedup registry".
		// Constants inline their value and emit no name, so they never reserve. Reference nodes
		// borrow another node's name, and Fetch-mode nodes consume an externally declared name.
		private bool ReservesUniformName
		{
			get
			{
				return m_currentParameterType != PropertyType.Constant
					&& m_variableMode != VariableMode.Fetch
					&& !IsPropertyReference;
			}
		}

		public PropertyNode PropertyReference
		{
			get
			{
				if( !IsPropertyReference )
					return null;

				if( m_propertyReference == null && m_propertyReferenceNodeId > -1 )
				{
					m_propertyReference = ContainerGraph.GetNode( m_propertyReferenceNodeId ) as PropertyNode;
				}

				if( m_propertyReference != null && ( m_propertyReference == this || m_propertyReference.GetType() != GetType() ) )
				{
					m_propertyReference = null;
				}

				return m_propertyReference;
			}
		}

		public void SetPropertyReference( PropertyNode reference )
		{
			if( !m_canBeReferenced || reference == null || reference == this || reference.GetType() != GetType() )
				return;

			if( m_propertyReferenceType != TexReferenceType.Instance )
			{
				m_propertyReferenceType = TexReferenceType.Instance;
				EnablePropertyReferenceMode();
			}

			m_propertyReference = reference;
			m_propertyReferenceNodeId = reference.UniqueId;

			// Leave Constant mode so material values are displayed like on the referenced node; not going through
			// ChangeParameterType() on purpose as referencing nodes must stay out of the property node registries
			if( m_currentParameterType == PropertyType.Constant && reference.CurrentParameterType != PropertyType.Constant )
			{
				m_currentParameterType = PropertyType.Property;
			}

			m_propertyNameIsDirty = true;
			PreviewIsDirty = true;
		}

		private void OnPropertyReferenceTypeChanged()
		{
			if( m_propertyReferenceType == TexReferenceType.Instance )
			{
				EnablePropertyReferenceMode();
			}
			else
			{
				DisablePropertyReferenceMode();
			}
		}

		private void EnablePropertyReferenceMode()
		{
			// Release ownership over current property name so other nodes may use it
			if( UIUtils.CheckUniformNameOwner( m_propertyName ) == UniqueId )
			{
				UIUtils.ReleaseUniformName( UniqueId, m_propertyName );
			}
			m_oldName = m_propertyName;

			if( UIUtils.IsProperty( m_currentParameterType ) )
			{
				UIUtils.UnregisterPropertyNode( this );
			}

			m_propertyReference = null;
			m_propertyReferenceNodeId = -1;
			m_lastPropertyReferenceTitle = string.Empty;
			m_lastPropertyReferenceValue = string.Empty;
			m_headerColorModifier = ReferenceHeaderColor;
			m_hasLeftDropdown = false;
			DropdownEditing = false;
		}

		private void DisablePropertyReferenceMode()
		{
			m_headerColorModifier = Color.white;
			m_hasLeftDropdown = m_freeType;
			m_propertyReference = null;
			m_propertyReferenceNodeId = -1;

			if( UIUtils.IsUniformNameAvailable( m_propertyName ) )
			{
				UIUtils.RegisterUniformName( UniqueId, m_propertyName );
				m_oldName = m_propertyName;
			}
			else if( UIUtils.CheckUniformNameOwner( m_propertyName ) != UniqueId )
			{
				RegisterFirstAvailablePropertyName( false );
			}

			if( UIUtils.IsProperty( m_currentParameterType ) )
			{
				UIUtils.RegisterPropertyNode( this );
			}

			m_propertyNameIsDirty = true;
		}

		private void RefreshPropertyReferenceCandidates()
		{
			m_propertyReferenceCandidates.Clear();
			List<PropertyNode> nodes = ContainerGraph.RawPropertyNodes.NodesList;
			int count = nodes.Count;
			for( int i = 0; i < count; i++ )
			{
				PropertyNode candidate = nodes[ i ];
				if( candidate != null && candidate != this && candidate.GetType() == GetType() && !candidate.IsPropertyReference )
				{
					m_propertyReferenceCandidates.Add( candidate );
				}
			}
		}

		private void DrawPropertyReferencePicker()
		{
			RefreshPropertyReferenceCandidates();
			int count = m_propertyReferenceCandidates.Count;
			string[] labels = new string[ count ];
			int currentIdx = -1;
			for( int i = 0; i < count; i++ )
			{
				labels[ i ] = m_propertyReferenceCandidates[ i ].PropertyInspectorName;
				if( m_propertyReferenceCandidates[ i ].UniqueId == m_propertyReferenceNodeId )
				{
					currentIdx = i;
				}
			}

			bool guiEnabledBuffer = GUI.enabled;
			GUI.enabled = count > 0;
			EditorGUI.BeginChangeCheck();
			currentIdx = EditorGUILayoutPopup( Constants.AvailableReferenceStr, currentIdx, labels );
			if( EditorGUI.EndChangeCheck() && currentIdx > -1 )
			{
				SetPropertyReference( m_propertyReferenceCandidates[ currentIdx ] );
			}
			GUI.enabled = guiEnabledBuffer;
		}

		protected virtual void CopyPropertyReferenceValues( PropertyNode reference ) { }

		void InitializeAttribsArray()
		{
			m_availableAttribsArr = new string[ m_availableAttribs.Count ];
			m_selectedAttribsArr = new bool[ m_availableAttribs.Count ];
			for( int i = 0; i < m_availableAttribsArr.Length; i++ )
			{
				m_availableAttribsArr[ i ] = m_availableAttribs[ i ].Name;
				m_selectedAttribsArr[ i ] = false;

				if( m_selectedAttribs.FindIndex( x => x == i ) > -1 )
				{
					m_selectedAttribsArr[ i ] = true;
					m_visibleAttribsFoldout = true;
				}
			}
		}

		public void SetAttribute( string name, bool state )
		{
			if( m_availableAttribsArr == null )
			{
				InitializeAttribsArray();
			}

			bool changed = false;
			for( int i = 0; i < m_availableAttribsArr.Length; i++ )
			{
				if ( m_availableAttribsArr[ i ] == name && m_selectedAttribsArr[ i ] != state )
				{
					m_selectedAttribsArr[ i ] = state;
					changed = true;
				}
			}

			if ( changed )
			{
				m_selectedAttribs.Clear();
				for( int i = 0; i < m_selectedAttribsArr.Length; i++ )
				{
					if( m_selectedAttribsArr[ i ] )
						m_selectedAttribs.Add( i );
				}

				OnAtrributesChanged();
			}
		}

		protected virtual void OnAtrributesChanged() { CheckEnumAttribute(); CheckHeaderAttribute(); }
		void DrawAttributesAddRemoveButtons()
		{
			if( m_availableAttribsArr == null )
			{
				InitializeAttribsArray();
			}

			int attribCount = m_selectedAttribs.Count;
			// Add new port
			if( GUILayout.Button( string.Empty, UIUtils.PlusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				m_visibleAttribsFoldout = true;
				m_selectedAttribs.Add( 0 );
				OnAtrributesChanged();
			}

			//Remove port
			if( GUILayout.Button( string.Empty, UIUtils.MinusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				if( attribCount > 0 )
				{
					m_selectedAttribs.RemoveAt( attribCount - 1 );
					OnAtrributesChanged();
				}
			}
		}

		void CheckEnumAttribute()
		{
			m_hasEnum = false;
			foreach( var item in m_selectedAttribs )
			{
				if( m_availableAttribsArr[ item ].Equals( "Enum" ) )
					m_hasEnum = true;
			}
		}



		protected void CheckHeaderAttribute()
		{
			m_hasHeaders = false;
			foreach( var item in m_selectedAttribs )
			{
				if( m_availableAttribsArr[ item ].Equals( HeaderId ) )
				{
					m_hasHeaders = true;
				}
			}
		}

		void DrawEnumAddRemoveButtons()
		{
			// Add new port
			if( GUILayout.Button( string.Empty, UIUtils.PlusStyle, GUILayout.Width( ButtonLayoutWidth ) ) && m_enumModeInt == 0 )
			{
				m_enumNames.Add( "Option" + ( m_enumValues.Count + 1 ) );
				m_enumValues.Add( m_enumValues.Count );
				m_enumCount++;
				m_visibleEnumsFoldout = true;
			}

			//Remove port
			if( GUILayout.Button( string.Empty, UIUtils.MinusStyle, GUILayout.Width( ButtonLayoutWidth ) ) && m_enumModeInt == 0 )
			{
				if( m_enumNames.Count - 1 > -1 )
				{
					m_enumNames.RemoveAt( m_enumNames.Count - 1 );
					m_enumValues.RemoveAt( m_enumValues.Count - 1 );
					m_enumCount--;
				}
			}
		}

		protected void DrawEnums()
		{
			m_enumModeInt = EditorGUILayout.IntPopup( "Mode", m_enumModeInt, EnumModesStr, EnumModeIntValues );

			if( m_enumModeInt == 0 )
			{
				if( m_enumNames.Count == 0 )
					EditorGUILayout.HelpBox( "Your list is Empty!\nUse the plus button to add more.", MessageType.Info );

				float cacheLabelSize = EditorGUIUtility.labelWidth;
				EditorGUIUtility.labelWidth = 50;

				for( int i = 0; i < m_enumNames.Count; i++ )
				{
					EditorGUI.BeginChangeCheck();
					EditorGUILayout.BeginHorizontal();
					m_enumNames[ i ] = EditorGUILayoutTextField( "Name", m_enumNames[ i ] );
					m_enumValues[ i ] = Mathf.Max( 0, EditorGUILayoutIntField( "Value", m_enumValues[ i ], GUILayout.Width( 100 ) ) );
					EditorGUILayout.EndHorizontal();
					if( EditorGUI.EndChangeCheck() )
					{
						m_enumNames[ i ] = UIUtils.RemoveInvalidEnumCharacters( m_enumNames[ i ] );
						if( string.IsNullOrEmpty( m_enumNames[ i ] ) )
						{
							m_enumNames[ i ] = "Option" + ( i + 1 );
						}
					}
				}

				EditorGUIUtility.labelWidth = cacheLabelSize;
				if( m_enumNames.Count > 0 )
				{
					EditorGUILayout.BeginHorizontal();
					GUILayout.Label( " " );
					DrawEnumAddRemoveButtons();
					EditorGUILayout.EndHorizontal();
				}
			}
			else
			{
				EditorGUILayout.BeginHorizontal();
				m_enumClassName = EditorGUILayoutTextField( "Class Name", m_enumClassName );

				if( GUILayout.Button( string.Empty, UIUtils.InspectorPopdropdownFallback, GUILayout.Width( 17 ), GUILayout.Height( 19 ) ) )
				{
					GenericMenu menu = new GenericMenu();
					AddMenuItem( menu, "UnityEngine.Rendering.CullMode" );
					AddMenuItem( menu, "UnityEngine.Rendering.ColorWriteMask" );
					AddMenuItem( menu, "UnityEngine.Rendering.CompareFunction" );
					AddMenuItem( menu, "UnityEngine.Rendering.StencilOp" );
					AddMenuItem( menu, "UnityEngine.Rendering.BlendMode" );
					AddMenuItem( menu, "UnityEngine.Rendering.BlendOp" );
					menu.ShowAsContext();
				}
				EditorGUILayout.EndHorizontal();
			}
		}

		private void AddMenuItem( GenericMenu menu, string newClass )
		{
			menu.AddItem( new GUIContent( newClass ), m_enumClassName.Equals( newClass ), OnSelection, newClass );
		}

		private void OnSelection( object newClass )
		{
			m_enumClassName = (string)newClass;
		}

		protected void DrawCustomAttrAddRemoveButtons()
		{
			// Add new port
			if( GUILayout.Button( string.Empty, UIUtils.PlusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				m_customAttr.Add( "" );
				m_customAttrCount++;
				//m_enumCount++;
				m_visibleCustomAttrFoldout = true;
			}

			//Remove port
			if( GUILayout.Button( string.Empty, UIUtils.MinusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				if( m_customAttr.Count - 1 > -1 )
				{
					m_customAttr.RemoveAt( m_customAttr.Count - 1 );
					m_customAttrCount--;
				}
			}
		}

		protected void DrawCustomAttributes()
		{
			for( int i = 0; i < m_customAttrCount; i++ )
			{
				EditorGUI.BeginChangeCheck();
				m_customAttr[ i ] = EditorGUILayoutTextField( "Attribute " + i, m_customAttr[ i ] );
				if( EditorGUI.EndChangeCheck() )
				{
					m_customAttr[ i ] = UIUtils.RemoveInvalidAttrCharacters( m_customAttr[ i ] );
				}
			}

			if( m_customAttrCount <= 0 )
			{
				EditorGUILayout.HelpBox( "Your list is Empty!\nUse the plus button to add more.", MessageType.Info );
				return;
			}

			EditorGUILayout.BeginHorizontal();
			GUILayout.Label( " " );
			DrawCustomAttrAddRemoveButtons();
			EditorGUILayout.EndHorizontal();
		}

		protected void DrawHeaderAttrAddRemoveButtons()
		{
			// Add new port
			if( GUILayout.Button( string.Empty, UIUtils.PlusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				m_headerAttributeValues.Add( "" );
				m_visibleHeaderAttrFoldout = true;
			}

			//Remove port
			if( GUILayout.Button( string.Empty, UIUtils.MinusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			{
				if( m_headerAttributeValues.Count > 0 )
				{
					m_headerAttributeValues.RemoveAt( m_headerAttributeValues.Count - 1 );
				}
			}
		}

		protected void DrawHeaderAttributes()
		{
			int count = m_headerAttributeValues.Count;
			for( int i = 0; i < count; i++ )
			{
				EditorGUI.BeginChangeCheck();
				m_headerAttributeValues[ i ] = EditorGUILayoutTextField( "Header " + i, m_headerAttributeValues[ i ] );
				if( EditorGUI.EndChangeCheck() )
				{
					m_headerAttributeValues[ i ] = UIUtils.RemoveHeaderAttrCharacters( m_headerAttributeValues[ i ] );
				}
			}




			if( count <= 0 )
			{
				EditorGUILayout.HelpBox( "Your list is Empty!\nUse the plus button to add more.", MessageType.Info );
				return;
			}

			EditorGUILayout.BeginHorizontal();
			GUILayout.Label( " " );
			DrawHeaderAttrAddRemoveButtons();
			EditorGUILayout.EndHorizontal();
		}


		public virtual void DrawAttributes()
		{
			int attribCount = m_selectedAttribs.Count;
			EditorGUI.BeginChangeCheck();
			if( m_availableAttribsArr == null )
			{
				InitializeAttribsArray();
			}
			for( int i = 0; i < m_availableAttribsArr.Length; i++ )
			{
				m_selectedAttribsArr[ i ] = EditorGUILayoutToggleLeft( m_availableAttribsArr[ i ], m_selectedAttribsArr[ i ] );
			}
			if( EditorGUI.EndChangeCheck() )
			{
				m_selectedAttribs.Clear();
				for( int i = 0; i < m_selectedAttribsArr.Length; i++ )
				{
					if( m_selectedAttribsArr[ i ] )
						m_selectedAttribs.Add( i );
				}

				OnAtrributesChanged();
			}

			bool customAttr = EditorGUILayoutToggleLeft( "Custom", m_customAttrCount == 0 ? false : true );
			if( !customAttr )
			{
				m_customAttrCount = 0;
			}
			else if( customAttr && m_customAttrCount < 1 )
			{
				if( m_customAttr.Count == 0 )
					m_customAttr.Add( "" );

				m_customAttrCount = m_customAttr.Count;
			}
			//m_customAttrCount = EditorGUILayoutToggleLeft( "Custom Attribute", m_customAttrCount == 0 ? false : true ) == 0 ? false : true;

			//if( attribCount == 0 )
			//{
			//	EditorGUILayout.HelpBox( "Your list is Empty!\nUse the plus button to add more.", MessageType.Info );
			//}

			//bool actionAllowed = true;
			//int deleteItem = -1;

			//for ( int i = 0; i < attribCount; i++ )
			//{
			//	EditorGUI.BeginChangeCheck();
			//	{
			//		m_selectedAttribs[ i ] = EditorGUILayoutPopup( m_selectedAttribs[ i ], m_availableAttribsArr );
			//	}
			//	if ( EditorGUI.EndChangeCheck() )
			//	{
			//		OnAtrributesChanged();
			//	}

			//	EditorGUILayout.BeginHorizontal();
			//	GUILayout.Label( " " );
			//	// Add After
			//	if ( GUILayout.Button( string.Empty, UIUtils.PlusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			//	{
			//		if ( actionAllowed )
			//		{
			//			m_selectedAttribs.Insert( i, m_selectedAttribs[ i ] );
			//			actionAllowed = false;
			//			OnAtrributesChanged();
			//		}
			//	}

			//	// Remove Current
			//	if ( GUILayout.Button( string.Empty, UIUtils.MinusStyle, GUILayout.Width( ButtonLayoutWidth ) ) )
			//	{
			//		if ( actionAllowed )
			//		{
			//			actionAllowed = false;
			//			deleteItem = i;
			//		}
			//	}
			//	EditorGUILayout.EndHorizontal();
			//}
			//if ( deleteItem > -1 )
			//{
			//	m_selectedAttribs.RemoveAt( deleteItem );
			//	OnAtrributesChanged();
			//}
		}
		public virtual void DrawMainPropertyBlock()
		{
			EditorGUI.BeginChangeCheck();
			EditorGUILayout.BeginVertical();
			{
				if( m_canBeReferenced && m_freeType )
				{
					EditorGUI.BeginChangeCheck();
					m_propertyReferenceType = (TexReferenceType)EditorGUILayoutPopup( Constants.ReferenceTypeStr, (int)m_propertyReferenceType, Constants.ReferenceArrayLabels );
					if( EditorGUI.EndChangeCheck() )
					{
						OnPropertyReferenceTypeChanged();
					}

					UIUtils.DrawSeparator();

					if( IsPropertyReference )
					{
						DrawPropertyReferencePicker();
						EditorGUILayout.EndVertical();
						if( EditorGUI.EndChangeCheck() )
						{
							OnDirtyProperty();
						}
						return;
					}
				}

				if( m_freeType )
				{
					PropertyType parameterType = (PropertyType)EditorGUILayoutEnumPopup( ParameterTypeStr, m_currentParameterType );
					if( parameterType != m_currentParameterType )
					{
						ChangeParameterType( parameterType );
						BeginPropertyFromInspectorCheck();
					}
				}

				if( m_freeName )
				{
					switch( m_currentParameterType )
					{
						case PropertyType.Property:
						case PropertyType.InstancedProperty:
						{
							ShowPropertyInspectorNameGUI();
							ShowPropertyNameGUI( true );
							ShowVariableMode();
							ShowHybridInstanced();
							ShowAutoRegister();
							ShowPrecision();
							ShowToolbar();
						}
						break;
						case PropertyType.Global:
						{
							ShowPropertyInspectorNameGUI();
							ShowPropertyNameGUI( false );
							ShowVariableMode();
							ShowAutoRegister();
							ShowPrecision();
							ShowDefaults();
						}
						break;
						case PropertyType.Constant:
						{
							ShowPropertyInspectorNameGUI();
							ShowPrecision();
							ShowDefaults();
						}
						break;
					}
				}
			}
			EditorGUILayout.EndVertical();
			if ( EditorGUI.EndChangeCheck() )
			{
				OnDirtyProperty();
			}
		}

		public void DrawMainPropertyBlockNoPrecision()
		{
			EditorGUILayout.BeginVertical();
			{
				if( m_freeType )
				{
					PropertyType parameterType = (PropertyType)EditorGUILayoutEnumPopup( ParameterTypeStr, m_currentParameterType );
					if( parameterType != m_currentParameterType )
					{
						ChangeParameterType( parameterType );
						BeginPropertyFromInspectorCheck();
					}
				}

				if( m_freeName )
				{
					switch( m_currentParameterType )
					{
						case PropertyType.Property:
						case PropertyType.InstancedProperty:
						{
							ShowPropertyInspectorNameGUI();
							ShowPropertyNameGUI( true );
							ShowToolbar();
						}
						break;
						case PropertyType.Global:
						{
							ShowPropertyInspectorNameGUI();
							ShowPropertyNameGUI( false );
							ShowDefaults();
						}
						break;
						case PropertyType.Constant:
						{
							ShowPropertyInspectorNameGUI();
							ShowDefaults();
						}
						break;
					}
				}
			}
			EditorGUILayout.EndVertical();
		}

		public override void DrawProperties()
		{
			base.DrawProperties();
			if( m_freeType || m_freeName )
			{
				NodeUtils.DrawPropertyGroup( ref m_propertiesFoldout, Constants.ParameterLabelStr, DrawMainPropertyBlock );
				if( m_drawAttributes && !IsPropertyReference )
					NodeUtils.DrawPropertyGroup( ref m_visibleAttribsFoldout, Constants.AttributesLaberStr, DrawAttributes );

				if( m_hasEnum && !IsPropertyReference )
				{
					if( m_enumModeInt == 0 )
						NodeUtils.DrawPropertyGroup( ref m_visibleEnumsFoldout, EnumsStr, DrawEnums, DrawEnumAddRemoveButtons );
					else
						NodeUtils.DrawPropertyGroup( ref m_visibleEnumsFoldout, EnumsStr, DrawEnums );
				}

				if( m_drawAttributes && !IsPropertyReference )
				{
					if( m_hasHeaders )
						NodeUtils.DrawPropertyGroup( ref m_visibleHeaderAttrFoldout, HeaderAttrStr, DrawHeaderAttributes, DrawHeaderAttrAddRemoveButtons );

					if( m_customAttrCount > 0 )
						NodeUtils.DrawPropertyGroup( ref m_visibleCustomAttrFoldout, CustomAttrStr, DrawCustomAttributes, DrawCustomAttrAddRemoveButtons );
				}

				if( !IsPropertyReference )
					PropertyNodeExtensions.DrawUI( this );

				CheckPropertyFromInspector();
			}
		}

		public void ShowPrecision()
		{
			if( m_drawPrecisionUI )
			{
				bool guiEnabled = GUI.enabled;
				GUI.enabled = ( m_currentParameterType == PropertyType.Constant || m_variableMode == VariableMode.Create ) && !m_disablePrecisionUI;
				EditorGUI.BeginChangeCheck();
				DrawPrecisionProperty();
				if( EditorGUI.EndChangeCheck() )
					m_precisionString = UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType );

				GUI.enabled = guiEnabled;

				if ( m_disablePrecisionUI )
				{
					EditorGUILayout.HelpBox( PrecisionDisabledWarningText, MessageType.Warning );
				}
			}
		}

		public void ShowToolbar()
		{
			//if ( !CanDrawMaterial )
			//{
			//	ShowDefaults();
			//	return;
			//}

			EditorGUILayout.BeginHorizontal();
			GUILayout.Space( 20 );
			m_propertyTab = GUILayout.Toolbar( m_propertyTab, LabelToolbarTitle );
			EditorGUILayout.EndHorizontal();
			switch( m_propertyTab )
			{
				default:
				case 0:
				{
					EditorGUI.BeginChangeCheck();
					DrawMaterialProperties();
					if( EditorGUI.EndChangeCheck() )
					{
						BeginDelayedDirtyProperty();
					}
				}
				break;
				case 1:
				{
					ShowDefaults();
				}
				break;
			}
		}

		public void ShowDefaults()
		{
			EditorGUI.BeginChangeCheck();
			DrawSubProperties();
			if( EditorGUI.EndChangeCheck() )
			{
				BeginDelayedDirtyProperty();
			}
			if( m_currentParameterType == PropertyType.Global && m_globalDefaultBehavior )
			{
				if( DebugConsoleWindow.DeveloperMode )
				{
					ShowGlobalValueButton();
				}
				EditorGUILayout.HelpBox( GlobalTypeWarningText, MessageType.Warning );
			}
		}

		public void ShowPropertyInspectorNameGUI()
		{
			EditorGUI.BeginChangeCheck();
			m_propertyInspectorName = EditorGUILayoutTextField( PropertyInspectorStr, m_propertyInspectorName );
			if( EditorGUI.EndChangeCheck() )
			{
				SetClippedTitle( m_propertyInspectorName, m_longNameSize );
				if( m_propertyInspectorName.Length > 0 )
				{
					BeginPropertyFromInspectorCheck();
				}
			}
		}

		public void ShowPropertyNameGUI( bool isProperty )
		{
			bool guiEnabledBuffer = GUI.enabled;
			if( isProperty )
			{
				EditorGUILayout.BeginHorizontal();
				GUI.enabled = !m_autoGlobalName;
				EditorGUI.BeginChangeCheck();
				m_propertyName = EditorGUILayoutTextField( PropertyNameStr, m_propertyName );
				if( EditorGUI.EndChangeCheck() )
				{
					//BeginPropertyFromInspectorCheck();
					m_checkDuplicateProperty = true;
					m_checkDuplicatePropertyTimestamp = EditorApplication.timeSinceStartup;
				}
				GUI.enabled = guiEnabledBuffer;
				EditorGUI.BeginChangeCheck();
				m_autoGlobalName = GUILayout.Toggle( m_autoGlobalName, ( m_autoGlobalName ? UIUtils.LockIconOpen : UIUtils.LockIconClosed ), "minibutton", GUILayout.Width( 22 ) );
				if( EditorGUI.EndChangeCheck() )
				{
					if( m_autoGlobalName )
						BeginPropertyFromInspectorCheck();
				}
				EditorGUILayout.EndHorizontal();
			}
			else
			{
				GUI.enabled = false;
				m_propertyName = EditorGUILayoutTextField( PropertyNameStr, m_propertyName );
				GUI.enabled = guiEnabledBuffer;
			}
		}

		public void ShowVariableMode()
		{
			if( m_showVariableMode || m_freeType )
				CurrentVariableMode = (VariableMode)EditorGUILayoutEnumPopup( IgnoreVarDeclarationStr, m_variableMode );
		}

		public void ShowHybridInstanced()
		{
			if( m_showHybridInstancedUI && CurrentParameterType == PropertyType.Property && ( m_containerGraph.IsSRP || m_containerGraph.CurrentShaderFunction != null ) )
			{
				m_hybridInstanced = EditorGUILayoutToggle( HybridInstancedStr, m_hybridInstanced );
			}
		}

		public void ShowAutoRegister()
		{
			if( m_showAutoRegisterUI && CurrentParameterType != PropertyType.Constant )
			{
				m_autoRegister = EditorGUILayoutToggle( AutoRegisterStr, m_autoRegister );
			}
		}

		public virtual string GetPropertyValStr() { return string.Empty; }
		public virtual string GetSubTitleVarNameFormatStr() { return Constants.SubTitleVarNameFormatStr; }

		public override bool OnClick( Vector2 currentMousePos2D )
		{
			bool singleClick = base.OnClick( currentMousePos2D );
			m_propertyTab = m_materialMode ? 0 : 1;
			return singleClick;
		}

		public override void OnNodeDoubleClicked( Vector2 currentMousePos2D )
		{
			if( currentMousePos2D.y - m_globalPosition.y > ( Constants.NODE_HEADER_HEIGHT + Constants.NODE_HEADER_EXTRA_HEIGHT ) * ContainerGraph.ParentWindow.CameraDrawInfo.InvertedZoom )
			{
				ContainerGraph.ParentWindow.ParametersWindow.IsMaximized = !ContainerGraph.ParentWindow.ParametersWindow.IsMaximized;
			}
		}

		public override void DrawTitle( Rect titlePos )
		{
			//base.DrawTitle( titlePos );
		}

		public override void Draw( DrawInfo drawInfo )
		{
			base.Draw( drawInfo );

			// Custom Editable Title
			if( ContainerGraph.LodLevel <= ParentGraph.NodeLOD.LOD3 )
			{
				if( !m_isEditing && !IsPropertyReference && ( ( !ContainerGraph.ParentWindow.MouseInteracted && drawInfo.CurrentEventType == EventType.MouseDown && m_titleClickArea.Contains( drawInfo.MousePosition ) ) ) )
				{
					if( ( EditorApplication.timeSinceStartup - m_clickTime ) < m_doubleClickTime )
						m_startEditing = true;
					else
						GUI.FocusControl( null );
					m_clickTime = EditorApplication.timeSinceStartup;
				}
				else if( m_isEditing && ( ( drawInfo.CurrentEventType == EventType.MouseDown && !m_titleClickArea.Contains( drawInfo.MousePosition ) ) || !EditorGUIUtility.editingTextField ) )
				{
					m_stopEditing = true;
				}

				if( m_isEditing || m_startEditing )
				{
					EditorGUI.BeginChangeCheck();
					GUI.SetNextControlName( m_uniqueName );
					m_propertyInspectorName = EditorGUITextField( m_titleClickArea, string.Empty, m_propertyInspectorName, UIUtils.GetCustomStyle( CustomStyle.NodeTitle ) );
					if( EditorGUI.EndChangeCheck() )
					{
						SetClippedTitle( m_propertyInspectorName, m_longNameSize );
						m_sizeIsDirty = true;
						m_isDirty = true;
						if( m_propertyInspectorName.Length > 0 )
						{
							BeginPropertyFromInspectorCheck();
						}
					}

					if( m_startEditing )
						EditorGUI.FocusTextInControl( m_uniqueName );
					//if( m_stopEditing )
					//	GUI.FocusControl( null );
				}

				if( drawInfo.CurrentEventType == EventType.Repaint )
				{
					if( m_startEditing )
					{
						m_startEditing = false;
						m_isEditing = true;
					}

					if( m_stopEditing )
					{
						m_stopEditing = false;
						m_isEditing = false;
						GUI.FocusControl( null );
					}
				}


				if( m_freeType )
				{
					if( m_dropdownEditing )
					{
						PropertyType parameterType = (PropertyType)EditorGUIEnumPopup( m_dropdownRect, m_currentParameterType, UIUtils.PropertyPopUp );
						if( parameterType != m_currentParameterType )
						{
							ChangeParameterType( parameterType );
							BeginPropertyFromInspectorCheck();
							DropdownEditing = false;
						}
					}
				}
			}

		}

		public override void OnNodeLayout( DrawInfo drawInfo, NodeUpdateCache cache )
		{
			//base.OnNodeLayout( drawInfo, cache );
			if( m_reRegisterName )
			{
				m_reRegisterName = false;
				if( ReservesUniformName )
					UIUtils.RegisterUniformName( UniqueId, m_propertyName );
			}

			CheckDelayedDirtyProperty();

			if( m_currentParameterType != m_lastParameterType || m_propertyNameIsDirty )
			{
				m_lastParameterType = m_currentParameterType;
				m_propertyNameIsDirty = false;
				OnDirtyProperty();
				if( IsPropertyReference && PropertyReference != null )
				{
					SetClippedTitle( PropertyReference.PropertyInspectorName, m_longNameSize );
					SetClippedAdditionalTitle( string.Format( Constants.SubTitleValueFormatStr, PropertyReference.GetPropertyValStr() ), m_longNameSize, LongNameEnder );
				}
				else if( m_currentParameterType != PropertyType.Constant )
				{
					SetClippedTitle( m_propertyInspectorName, m_longNameSize );
					//bool globalHandler = false;
					//if( globalHandler )
					//{
					string currValue = ( m_currentParameterType == PropertyType.Global && m_globalDefaultBehavior ) ? "<GLOBAL>" : GetPropertyValStr();
					SetClippedAdditionalTitle( string.Format( m_useVarSubtitle ? GetSubTitleVarNameFormatStr() : Constants.SubTitleValueFormatStr, currValue ), m_longNameSize, LongNameEnder );
					//}
					//else
					//{
					//	if( m_currentParameterType == PropertyType.Global )
					//	{
					//		SetAdditonalTitleText( "Global" );
					//	}
					//	else
					//	{
					//		SetAdditonalTitleText( string.Format( m_useVarSubtitle ? GetSubTitleVarNameFormatStr() : Constants.SubTitleValueFormatStr, GetPropertyValStr() ) );
					//	}
					//}
				}
				else
				{
					SetClippedTitle( m_propertyInspectorName, m_longNameSize );
					SetClippedAdditionalTitle( string.Format( Constants.SubTitleConstFormatStr, GetPropertyValStr() ), m_longNameSize, LongNameEnder );
				}
			}

			CheckPropertyFromInspector();
			CheckDuplicateProperty();
			// RUN LAYOUT CHANGES AFTER TITLES CHANGE
			base.OnNodeLayout( drawInfo, cache );

			m_titleClickArea = m_titlePos;
			m_titleClickArea.height = Constants.NODE_HEADER_HEIGHT;

			if( IsPropertyReference )
			{
				m_referenceIconPos = m_globalPosition;
				m_referenceIconPos.width = ReferenceIconSize * drawInfo.InvertedZoom;
				m_referenceIconPos.height = ReferenceIconSize * drawInfo.InvertedZoom;
				m_referenceIconPos.y += 10 * drawInfo.InvertedZoom;
				m_referenceIconPos.x += 5 * drawInfo.InvertedZoom;
			}
		}

		public override void OnNodeRepaint( DrawInfo drawInfo )
		{
			base.OnNodeRepaint( drawInfo );

			if( !m_isVisible )
				return;

			// Fixed Title ( only renders when not editing )
			if( m_showTitleWhenNotEditing && !m_isEditing && !m_startEditing && ContainerGraph.LodLevel <= ParentGraph.NodeLOD.LOD3 )
			{
				GUI.Label( m_titleClickArea, m_content, UIUtils.GetCustomStyle( CustomStyle.NodeTitle ) );
			}

			if( IsPropertyReference )
			{
				GUI.Label( m_referenceIconPos, string.Empty, UIUtils.GetCustomStyle( CustomStyle.SamplerTextureIcon ) );
			}
		}

		public void RegisterFirstAvailablePropertyName( bool releaseOldOne, bool appendIndexToCurrOne = false )
		{
			if( releaseOldOne )
				UIUtils.ReleaseUniformName( UniqueId, m_oldName );

			if( !ReservesUniformName )
			{
				// C: constants (and fetch / reference nodes) don't reserve a slot. Give a plain default
				// pair only when none exists yet; never scan for uniqueness or register.
				if( string.IsNullOrEmpty( m_propertyName ) )
					UIUtils.GetDefaultName( m_outputPorts[ 0 ].DataType, out m_propertyName, out m_propertyInspectorName, !string.IsNullOrEmpty( m_customPrefix ), m_customPrefix );
			}
			else if( m_isNodeBeingCopied || appendIndexToCurrOne )
			{
				if( string.IsNullOrEmpty( m_propertyName ) )
					return;

				string newPropertyName = UIUtils.GetUniqueUniformName( m_propertyName );
				if( newPropertyName != m_propertyName )
				{
					UIUtils.RegisterUniformName( UniqueId, newPropertyName );
					m_propertyName = newPropertyName;
					// B: keep the inspector name in lockstep with the deduped uniform.
					m_propertyInspectorName = newPropertyName.StartsWith( "_" ) ? newPropertyName.Substring( 1 ) : newPropertyName;
				}
				else
				{
					if( UIUtils.IsUniformNameAvailable( m_propertyName ) )
						UIUtils.RegisterUniformName( UniqueId, m_propertyName );
					else
						UIUtils.GetFirstAvailableName( UniqueId, m_outputPorts[ 0 ].DataType, out m_propertyName, out m_propertyInspectorName, !string.IsNullOrEmpty( m_customPrefix ), m_customPrefix );
				}

			}
			else
			{
				UIUtils.GetFirstAvailableName( UniqueId, m_outputPorts[ 0 ].DataType, out m_propertyName, out m_propertyInspectorName, !string.IsNullOrEmpty( m_customPrefix ), m_customPrefix );
			}
			m_oldName = m_propertyName;
			m_propertyNameIsDirty = true;
			m_reRegisterName = false;
			OnPropertyNameChanged();
		}

		public void SetRawPropertyName( string name )
		{
			m_propertyName = name;
		}

		public void RegisterPropertyName( bool releaseOldOne, string newName, bool autoGlobal = true, bool forceUnderscore = false )
		{
			if( m_currentParameterType != PropertyType.Constant && m_variableMode == VariableMode.Fetch )
			{
				string localPropertyName = string.Empty;
				if( autoGlobal )
					localPropertyName = UIUtils.GeneratePropertyName( newName, m_currentParameterType, forceUnderscore );
				else
				{
					localPropertyName = UIUtils.GeneratePropertyName( m_propertyName, PropertyType.Global, forceUnderscore );
					if( UIUtils.IsNumericName( localPropertyName ) )
					{
						m_propertyName = m_oldName;
					}

				}

				m_propertyName = localPropertyName;
				m_propertyInspectorName = newName;
				m_propertyNameIsDirty = true;
				m_reRegisterName = false;
				OnPropertyNameChanged();
				return;
			}

			string propertyName = string.Empty;
			if( autoGlobal )
				propertyName = UIUtils.GeneratePropertyName( newName, m_currentParameterType, forceUnderscore );
			else
			{
				propertyName = UIUtils.GeneratePropertyName( m_propertyName, PropertyType.Global, forceUnderscore );
				if( UIUtils.IsNumericName( propertyName ) )
				{
					m_propertyName = m_oldName;
				}
			}

			if( m_propertyName.Equals( propertyName ) )
				return;

			// Non-reserving nodes (constants) accept any display name without touching the registry;
			// reserving nodes need the name to be free.
			if( !ReservesUniformName || UIUtils.IsUniformNameAvailable( propertyName ) || m_allowPropertyDuplicates )
			{
				if( releaseOldOne )
					UIUtils.ReleaseUniformName( UniqueId, m_oldName );

				m_oldName = propertyName;
				m_propertyName = propertyName;
				if( autoGlobal )
					m_propertyInspectorName = newName;
				m_propertyNameIsDirty = true;
				m_reRegisterName = false;
				if( ReservesUniformName )
					UIUtils.RegisterUniformName( UniqueId, propertyName );
				OnPropertyNameChanged();
			}
			else
			{
				GUI.FocusControl( string.Empty );
				RegisterFirstAvailablePropertyName( releaseOldOne );
				UIUtils.ShowMessage( UniqueId, string.Format( "Property name '{0}' is already in use. Reverting to last valid name '{1}'", propertyName, m_oldName ) );
			}
		}

		protected string CreateLocalVarDec( string value )
		{
			return string.Format( Constants.PropertyLocalVarDec, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType ), m_propertyName, value );
		}

		public virtual void CheckIfAutoRegister( ref MasterNodeDataCollector dataCollector )
		{
			// Also testing inside shader function because node can be used indirectly over a custom expression and directly over a Function Output node
			// That isn't being used externaly making it to not be registered ( since m_connStatus it set to Connected by being connected to an output node
			if( CurrentParameterType != PropertyType.Constant && m_autoRegister && !IsPropertyReference && ( m_connStatus != NodeConnectionStatus.Connected || InsideShaderFunction ) )
			{
				RegisterProperty( ref dataCollector );
			}
		}

		virtual protected void RegisterProperty( ref MasterNodeDataCollector dataCollector )
		{
			CheckPropertyFromInspector( true );
			if( m_propertyName.Length == 0 )
			{
				RegisterFirstAvailablePropertyName( false );
			}

			switch( CurrentParameterType )
			{
				case PropertyType.Property:
				{
					if( m_variableMode == VariableMode.Create )
						dataCollector.AddToProperties( UniqueId, GetPropertyValue(), OrderIndex );
					string dataType = string.Empty;
					string dataName = string.Empty;
					bool fullValue = false;
					if( m_variableMode == VariableMode.Create && GetUniformData( out dataType, out dataName, ref fullValue ) )
					{
						if( fullValue )
						{
							dataCollector.AddToUniforms( UniqueId, dataName, m_srpBatcherCompatible );
						}
						else
						{
							dataCollector.AddToUniforms( UniqueId, dataType, dataName, m_srpBatcherCompatible, m_excludeUniform );
						}
					}

					if( m_hybridInstanced && dataCollector.IsTemplate && dataCollector.IsSRP )
					{
						dataCollector.AddToDotsProperties( m_outputPorts[ 0 ].DataType, UniqueId, m_propertyName, OrderIndex, CurrentPrecisionType );
					}
					//dataCollector.AddToUniforms( m_uniqueId, GetUniformValue() );
				}
				break;
				case PropertyType.InstancedProperty:
				{
					dataCollector.AddToPragmas( UniqueId, IOUtils.InstancedPropertiesHeader );

					if( m_registerPropertyOnInstancing )
						dataCollector.AddToProperties( UniqueId, GetPropertyValue(), OrderIndex );

					dataCollector.AddToInstancedProperties( m_outputPorts[ 0 ].DataType, UniqueId, GetInstancedUniformValue( dataCollector.IsTemplate, dataCollector.IsSRP ), OrderIndex );
				}
				break;
				case PropertyType.Global:
				{
					string dataType = string.Empty;
					string dataName = string.Empty;
					bool fullValue = false;
					if( m_variableMode == VariableMode.Create && GetUniformData( out dataType, out dataName, ref fullValue ) )
					{
						if( fullValue )
						{
							dataCollector.AddToUniforms( UniqueId, dataName, m_addGlobalToSRPBatcher );
						}
						else
						{
							dataCollector.AddToUniforms( UniqueId, dataType, dataName, m_addGlobalToSRPBatcher, m_excludeUniform );
						}
					}
					//dataCollector.AddToUniforms( m_uniqueId, GetUniformValue() );
				}
				break;
				case PropertyType.Constant: break;
			}
			dataCollector.AddPropertyNode( this );
			if( m_currentParameterType == PropertyType.InstancedProperty && !m_outputPorts[ 0 ].IsLocalValue( dataCollector.PortCategory ) )
			{
				string instancedVar = dataCollector.IsSRP ?
					//m_propertyName :
					string.Format( IOUtils.LWSRPInstancedPropertiesData, dataCollector.InstanceBlockName, m_propertyName ) :
					string.Format( IOUtils.InstancedPropertiesData, m_propertyName );

				bool insideSF = InsideShaderFunction;
				ParentGraph cachedGraph = ContainerGraph.ParentWindow.CustomGraph;
				if( insideSF )
					ContainerGraph.ParentWindow.CustomGraph = this.ContainerGraph;

				RegisterLocalVariable( 0, instancedVar, ref dataCollector, m_propertyName + "_Instance" );

				if( insideSF )
					ContainerGraph.ParentWindow.CustomGraph = cachedGraph;
			}
		}

		public override string GenerateShaderForOutput( int outputId, ref MasterNodeDataCollector dataCollector, bool ignoreLocalvar )
		{
			RegisterProperty( ref dataCollector );
			return string.Empty;
		}

		public override void Destroy()
		{
			base.Destroy();
			UIUtils.UnregisterRawPropertyNode( this );
			if( !string.IsNullOrEmpty( m_propertyName ) && UniqueId >= 0 )
				UIUtils.ReleaseUniformName( UniqueId, m_propertyName );

			if( m_currentParameterType == PropertyType.InstancedProperty )
			{
				UIUtils.CurrentWindow.OutsideGraph.RemoveInstancePropertyCount();
				UIUtils.UnregisterPropertyNode( this );
			}

			if( m_currentParameterType == PropertyType.Property )
			{
				UIUtils.UnregisterPropertyNode( this );
			}

			if( m_availableAttribs != null )
				m_availableAttribs.Clear();

			m_availableAttribs = null;

			m_propertyReference = null;
			m_propertyReferenceCandidates.Clear();
		}
		private const string HeaderFormatStr = "[Header({0})]";
		string BuildHeader()
		{
			string result = string.Empty;
			for( int i = 0; i < m_headerAttributeValues.Count; i++ )
			{
				result += string.Format( HeaderFormatStr, m_headerAttributeValues[ i ] );
			}
			return result;
		}

		string BuildEnum()
		{
			string result = "[Enum(";
			if( m_enumModeInt == 0 )
			{
				for( int i = 0; i < m_enumNames.Count; i++ )
				{
					result += m_enumNames[ i ] + "," + m_enumValues[ i ];
					if( i + 1 < m_enumNames.Count )
						result += ",";
				}
			}
			else
			{
				result += m_enumClassName;
			}
			result += ")]";
			return result;
		}

		// Canonical serialized form of the external integration payload (see PropertyNodeExtensions).
		public string ExtensionData
		{
			get { return m_extensionData; }
			set { m_extensionData = value; }
		}

		public string PropertyAttributes
		{
			get
			{
				int attribCount = m_selectedAttribs.Count;

				string attribs = string.Empty;
				for( int i = 0; i < attribCount; i++ )
				{
					if( m_availableAttribs[ m_selectedAttribs[ i ] ].Name.Equals( "Enum" ) )
						attribs += BuildEnum();
					else if( m_availableAttribs[ m_selectedAttribs[ i ] ].Name.Equals( HeaderId ) )
						attribs += BuildHeader();
					else
						attribs += m_availableAttribs[ m_selectedAttribs[ i ] ].Attribute;
				}

				for( int i = 0; i < m_customAttrCount; i++ )
				{
					if( !string.IsNullOrEmpty( m_customAttr[ i ] ) )
						attribs += "[" + m_customAttr[ i ] + "]";
				}

				attribs += PropertyNodeExtensions.BuildAttributes( this );
				return attribs;
			}
		}
		public string PropertyAttributesSeparator
		{
			get
			{
				return string.IsNullOrEmpty( PropertyAttributes ) ? string.Empty : " ";
			}
		}

		public virtual void OnDirtyProperty()
		{
			// @diogo: Look for an existing global property hard coded into the template. If found,
			//         disable precision control and force a precision type matching the template.

			m_disablePrecisionUI = false;

			if ( m_currentParameterType != PropertyType.Constant )
			{
				var templateMasterNode = ContainerGraph.CurrentMasterNode as TemplateMultiPassMasterNode;
				if ( templateMasterNode != null && templateMasterNode.CurrentTemplate != null )
				{
					var data = templateMasterNode.CurrentTemplate.GetGlobalShaderPropertyData( m_propertyName );
					if ( data != null )
					{
						m_currentPrecisionType = data.PrecisionType;
						m_disablePrecisionUI = true;
					}
				}
			}
		}

		public virtual void OnPropertyNameChanged() { UIUtils.UpdatePropertyDataNode( UniqueId, PropertyInspectorName ); }
		public virtual void DrawSubProperties() { }
		public virtual void DrawMaterialProperties() { }

		public virtual string GetPropertyValue() { return string.Empty; }
		public virtual string GetDefaultValue() { return string.Empty; }

		public string GetInstancedUniformValue( bool isTemplate, bool isSRP )
		{
			if( isTemplate )
			{
				if( isSRP )
				{
					return string.Format( IOUtils.LWSRPInstancedPropertiesElement, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType ), m_propertyName );
					//return GetUniformValue();
				}
				else
				{
					return string.Format( IOUtils.InstancedPropertiesElement, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType ), m_propertyName );
				}
			}
			else
				return string.Format( IOUtils.InstancedPropertiesElementTabs, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType ), m_propertyName );
		}

		public string GetInstancedUniformValue( bool isTemplate, bool isSRP, WirePortDataType dataType, string value )
		{
			if( isTemplate )
			{
				if( isSRP )
				{
					//return GetUniformValue( dataType, value );
					return string.Format( IOUtils.LWSRPInstancedPropertiesElement, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, dataType ), value );
				}
				else
				{
					return string.Format( IOUtils.InstancedPropertiesElement, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, dataType ), value );
				}
			}
			else
				return string.Format( IOUtils.InstancedPropertiesElementTabs, UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, dataType ), value );
		}

		public virtual string GetUniformValue()
		{
			bool excludeUniformKeyword = ( m_currentParameterType == PropertyType.InstancedProperty ) ||
											m_containerGraph.IsSRP;
			int index = excludeUniformKeyword ? 1 : 0;
			return string.Format( Constants.UniformDec[ index ], UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType ), m_propertyName );
		}

		public string GetUniformValue( WirePortDataType dataType, string value )
		{
			bool excludeUniformKeyword = ( m_currentParameterType == PropertyType.InstancedProperty ) ||
											m_containerGraph.IsSRP;
			int index = excludeUniformKeyword ? 1 : 0;
			return string.Format( Constants.UniformDec[ index ], UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, dataType ), value );
		}

		public virtual bool GetUniformData( out string dataType, out string dataName, ref bool fullValue )
		{
			dataType = UIUtils.PrecisionWirePortToCgType( CurrentPrecisionType, m_outputPorts[ 0 ].DataType );
			dataName = m_propertyName;
			fullValue = false;
			return true;
		}

		public PropertyType CurrentParameterType
		{
			get { return m_currentParameterType; }
			set { m_currentParameterType = value; }
		}

		public override void WriteToString( ref string nodeInfo, ref string connectionsInfo )
		{
			base.WriteToString( ref nodeInfo, ref connectionsInfo );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_currentParameterType );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_propertyName );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_propertyInspectorName );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_orderIndex );
			int attribCount = m_selectedAttribs.Count;
			IOUtils.AddFieldValueToString( ref nodeInfo, attribCount );
			if( attribCount > 0 )
			{
				for( int i = 0; i < attribCount; i++ )
				{
					IOUtils.AddFieldValueToString( ref nodeInfo, m_availableAttribs[ m_selectedAttribs[ i ] ].Attribute );
				}
			}
			IOUtils.AddFieldValueToString( ref nodeInfo, m_variableMode );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_autoGlobalName );

			int headerCount = m_headerAttributeValues.Count;
			IOUtils.AddFieldValueToString( ref nodeInfo, headerCount );
			for( int i = 0; i < headerCount; i++ )
			{
				IOUtils.AddFieldValueToString( ref nodeInfo, m_headerAttributeValues[ i ] );
			}

			IOUtils.AddFieldValueToString( ref nodeInfo, m_enumCount );
			for( int i = 0; i < m_enumCount; i++ )
			{
				IOUtils.AddFieldValueToString( ref nodeInfo, m_enumNames[ i ] );
				IOUtils.AddFieldValueToString( ref nodeInfo, m_enumValues[ i ] );
			}
			IOUtils.AddFieldValueToString( ref nodeInfo, m_enumModeInt );
			if( m_enumModeInt == 1 )
				IOUtils.AddFieldValueToString( ref nodeInfo, m_enumClassName );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_autoRegister );

			IOUtils.AddFieldValueToString( ref nodeInfo, m_customAttrCount );
			if( m_customAttrCount > 0 )
			{
				for( int i = 0; i < m_customAttrCount; i++ )
				{
					IOUtils.AddFieldValueToString( ref nodeInfo, m_customAttr[ i ] );
				}
			}

			IOUtils.AddFieldValueToString( ref nodeInfo, m_hybridInstanced );

			if( m_canBeReferenced )
			{
				IOUtils.AddFieldValueToString( ref nodeInfo, m_propertyReferenceType );
				IOUtils.AddFieldValueToString( ref nodeInfo, ( PropertyReference != null ) ? m_propertyReference.UniqueId : -1 );
			}

			// @diogo: opaque per-node extension payload (see PropertyNodeExtensions). Always written so the
			//         stream stays in sync; empty when no integration is registered.
			m_extensionData = PropertyNodeExtensions.WriteData( this, m_extensionData );
			IOUtils.AddFieldValueToString( ref nodeInfo, m_extensionData );
		}

		int IdForAttrib( string name )
		{
			int attribCount = m_availableAttribs.Count;
			for( int i = 0; i < attribCount; i++ )
			{
				if( m_availableAttribs[ i ].Attribute.Equals( name ) ||
					(m_availableAttribs[ i ].HasDeprecatedValue && m_availableAttribs[ i ].DeprecatedValue.Equals( name ) ) )
					return i;
			}
			return -1;
		}

		public override void ReadFromString( ref string[] nodeParams )
		{
			base.ReadFromString( ref nodeParams );
			if( UIUtils.CurrentShaderVersion() < 2505 )
			{
				string property = GetCurrentParam( ref nodeParams );
				m_currentParameterType = property.Equals( "Uniform" ) ? PropertyType.Global : (PropertyType)Enum.Parse( typeof( PropertyType ), property );
			}
			else
			{
				m_currentParameterType = (PropertyType)Enum.Parse( typeof( PropertyType ), GetCurrentParam( ref nodeParams ) );
			}

			if( m_currentParameterType == PropertyType.InstancedProperty )
			{
				UIUtils.CurrentWindow.OutsideGraph.AddInstancePropertyCount();
				UIUtils.RegisterPropertyNode( this );
			}

			if( m_currentParameterType == PropertyType.Property )
			{
				UIUtils.RegisterPropertyNode( this );
			}

			m_propertyName = GetCurrentParam( ref nodeParams );
			m_propertyInspectorName = GetCurrentParam( ref nodeParams );

			if( UIUtils.CurrentShaderVersion() > 13 )
			{
				m_orderIndex = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
			}

			if( UIUtils.CurrentShaderVersion() > 4102 )
			{
				int attribAmount = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
				if( attribAmount > 0 )
				{
					for( int i = 0; i < attribAmount; i++ )
					{
						string attribute = GetCurrentParam( ref nodeParams );
						int idForAttribute = IdForAttrib( attribute );
						if( idForAttribute >= 0 )
						{
							m_selectedAttribs.Add( idForAttribute );
						}
						else
						{
							UIUtils.ShowMessage( UniqueId, string.Format( InvalidAttributeFormatter, attribute,m_propertyInspectorName ) , MessageSeverity.Warning );
						}
					}

					m_visibleAttribsFoldout = true;
				}
				InitializeAttribsArray();
			}


			if( UIUtils.CurrentShaderVersion() > 14003 )
			{
				m_variableMode = (VariableMode)Enum.Parse( typeof( VariableMode ), GetCurrentParam( ref nodeParams ) );
			}

			if( UIUtils.CurrentShaderVersion() > 14201 )
			{
				m_autoGlobalName = Convert.ToBoolean( GetCurrentParam( ref nodeParams ) );
			}

			if( UIUtils.CurrentShaderVersion() > 18707 )
			{
				if( UIUtils.CurrentShaderVersion() == 18708 )
				{
					m_headerAttributeValues.Add( GetCurrentParam( ref nodeParams ) );
				}
				else
				{
					int headerCount = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
					for( int i = 0; i < headerCount; i++ )
					{
						m_headerAttributeValues.Add( GetCurrentParam( ref nodeParams ) );
					}
				}
			}

			if( UIUtils.CurrentShaderVersion() > 14403 )
			{
				m_enumCount = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
				for( int i = 0; i < m_enumCount; i++ )
				{
					m_enumNames.Add( GetCurrentParam( ref nodeParams ) );
					m_enumValues.Add( Convert.ToInt32( GetCurrentParam( ref nodeParams ) ) );
				}
			}

			if( UIUtils.CurrentShaderVersion() > 14501 )
			{
				m_enumModeInt = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
				if( m_enumModeInt == 1 )
					m_enumClassName = GetCurrentParam( ref nodeParams );
				m_autoRegister = Convert.ToBoolean( GetCurrentParam( ref nodeParams ) );

				m_customAttrCount = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
				for( int i = 0; i < m_customAttrCount; i++ )
				{
					m_customAttr.Add( GetCurrentParam( ref nodeParams ) );
				}
				if( m_customAttrCount > 0 )
				{
					m_visibleCustomAttrFoldout = true;
					m_visibleAttribsFoldout = true;
				}
			}

			if( UIUtils.CurrentShaderVersion() > 18003 )
			{
				m_hybridInstanced = Convert.ToBoolean( GetCurrentParam( ref nodeParams ) );
			}

			if( m_canBeReferenced && UIUtils.CurrentShaderVersion() > 19909 )
			{
				m_propertyReferenceType = (TexReferenceType)Enum.Parse( typeof( TexReferenceType ), GetCurrentParam( ref nodeParams ) );
				m_propertyReferenceNodeId = Convert.ToInt32( GetCurrentParam( ref nodeParams ) );
				if( IsPropertyReference )
				{
					m_headerColorModifier = ReferenceHeaderColor;
					m_hasLeftDropdown = false;
					if( UIUtils.IsProperty( m_currentParameterType ) )
					{
						UIUtils.UnregisterPropertyNode( this );
					}
				}
			}

			if( UIUtils.CurrentShaderVersion() >= 19910 )
			{
				m_extensionData = GetCurrentParam( ref nodeParams );
				PropertyNodeExtensions.ReadData( this, m_extensionData );
			}

			CheckEnumAttribute();
			CheckHeaderAttribute();
			if( m_enumCount > 0 )
				m_visibleEnumsFoldout = true;

			m_propertyNameIsDirty = true;
			m_reRegisterName = false;

			if( !m_isNodeBeingCopied )
			{
				if( m_variableMode != VariableMode.Fetch || m_currentParameterType == PropertyType.Constant )
				{
					if( IsPropertyReference )
					{
						// Referencing nodes don't own a property name; release the auto-registered one
						UIUtils.ReleaseUniformName( UniqueId, m_oldName );
						m_oldName = m_propertyName;
					}
					else
					{
						UIUtils.ReleaseUniformName( UniqueId, m_oldName );
						// C: constants keep their saved name but never reserve a registry slot.
						if( m_currentParameterType != PropertyType.Constant )
							UIUtils.RegisterUniformName( UniqueId, m_propertyName );
						m_oldName = m_propertyName;
					}
				}
			}
			else
			{
				m_oldName = m_propertyName;
			}

			ReleaseRansomedProperty();

		}

		// Preserve the property foldout states across a snapshot-undo reload; without this they are
		// lost ( the attribute foldout, for instance, only re-derives open from ReadFromString when
		// the node still has attributes, so undoing the last attribute would otherwise collapse it ).
		public override void WriteUndoViewState( List<string> data )
		{
			base.WriteUndoViewState( data );
			data.Add( m_visibleAttribsFoldout ? "1" : "0" );
			data.Add( m_visibleEnumsFoldout ? "1" : "0" );
			data.Add( m_visibleCustomAttrFoldout ? "1" : "0" );
			data.Add( m_visibleHeaderAttrFoldout ? "1" : "0" );
		}

		public override void ReadUndoViewState( string[] data, ref int index )
		{
			base.ReadUndoViewState( data, ref index );
			m_visibleAttribsFoldout = ReadUndoViewBool( data, ref index, m_visibleAttribsFoldout );
			m_visibleEnumsFoldout = ReadUndoViewBool( data, ref index, m_visibleEnumsFoldout );
			m_visibleCustomAttrFoldout = ReadUndoViewBool( data, ref index, m_visibleCustomAttrFoldout );
			m_visibleHeaderAttrFoldout = ReadUndoViewBool( data, ref index, m_visibleHeaderAttrFoldout );
		}

		public override void ReconnectClipboardReferences( Clipboard clipboard )
		{
			base.ReconnectClipboardReferences( clipboard );
			if( m_canBeReferenced && m_propertyReferenceNodeId > -1 )
			{
				int newId = clipboard.GeNewNodeId( m_propertyReferenceNodeId );
				if( newId > -1 )
				{
					m_propertyReferenceNodeId = newId;
				}
				m_propertyReference = null;
			}
		}

		public override void RefreshExternalReferences()
		{
			base.RefreshExternalReferences();
			if( IsPropertyReference )
			{
				m_propertyReference = null;
				m_headerColorModifier = ReferenceHeaderColor;
				m_hasLeftDropdown = false;
			}
		}

		public virtual void ReleaseRansomedProperty()
		{
			if( m_variableMode == VariableMode.Fetch/* && m_autoGlobalName */)
			{
				//Fooling setter to have a different value
				m_variableMode = VariableMode.Create;
				CurrentVariableMode = VariableMode.Fetch;
			}
		}

		void UpdateTooltip()
		{
			string currValue = string.Empty;
			if( m_currentParameterType != PropertyType.Constant )
			{
				currValue = ( m_currentParameterType == PropertyType.Global && m_globalDefaultBehavior ) ? "<GLOBAL>" : GetPropertyValStr();
			}
			else
			{
				currValue = GetPropertyValStr();
			}

			m_tooltipText = string.Format( TooltipFormatter, m_nodeAttribs.Description, m_propertyInspectorName, currValue );
		}

		public override void SetClippedTitle( string newText, int maxSize = 170, string endString = "..." )
		{
			base.SetClippedTitle( newText, maxSize, endString );
			UpdateTooltip();
		}

		public override void SetClippedAdditionalTitle( string newText, int maxSize = 170, string endString = "..." )
		{
			base.SetClippedAdditionalTitle( newText, maxSize, endString );
			UpdateTooltip();
		}

		public override void OnEnable()
		{
			base.OnEnable();
			m_reRegisterName = true;
		}

		public bool CanDrawMaterial { get { return m_materialMode && m_currentParameterType != PropertyType.Constant; } }
		public int RawOrderIndex
		{
			get { return m_orderIndex; }
		}

		public int OrderIndex
		{
			get { return m_orderIndex + m_orderIndexOffset; }
			set { m_orderIndex = value; }
		}

		public int OrderIndexOffset
		{
			get { return m_orderIndexOffset; }
			set { m_orderIndexOffset = value; }
		}

		public VariableMode CurrentVariableMode
		{
			get { return m_variableMode; }
			set
			{
				if( value != m_variableMode )
				{
					m_variableMode = value;
					if( value == VariableMode.Fetch )
					{
						// Release ownership on name
						if( UIUtils.CheckUniformNameOwner( m_oldName ) == UniqueId )
						{
							UIUtils.ReleaseUniformName( UniqueId , m_oldName );
						}
						m_oldName = m_propertyName;
					}
					else
					{
						if( !ReservesUniformName )
						{
							// C: constant / reference nodes don't hold a registry slot on Create.
							if( UIUtils.CheckUniformNameOwner( m_oldName ) == UniqueId )
								UIUtils.ReleaseUniformName( UniqueId, m_oldName );
							m_oldName = m_propertyName;
						}
						else if( !m_propertyName.Equals( m_oldName ) )
						{
							if( UIUtils.IsUniformNameAvailable( m_propertyName ) )
							{
								UIUtils.ReleaseUniformName( UniqueId, m_oldName );
								UIUtils.RegisterUniformName( UniqueId, m_propertyName );
							}
							else
							{
								UIUtils.ShowMessage( UniqueId, string.Format( FetchToCreateDuplicatesMsg, m_propertyName, m_oldName ), MessageSeverity.Warning );
								m_propertyName = m_oldName;
							}
							m_propertyNameIsDirty = true;
							OnPropertyNameChanged();
						}
						else
						{
							if( UIUtils.IsUniformNameAvailable( m_propertyName ) )
							{
								UIUtils.RegisterUniformName( UniqueId , m_propertyName );
							}
							else if( UIUtils.CheckUniformNameOwner( m_propertyName ) != UniqueId )
							{
								string oldProperty = m_propertyName;
								RegisterFirstAvailablePropertyName( false );
								UIUtils.ShowMessage( UniqueId, string.Format( FetchToCreateOnDuplicateNodeMsg, m_propertyName, oldProperty ), MessageSeverity.Warning );
							}
						}
					}
				}
			}
		}

		public string PropertyData( MasterNodePortCategory portCategory )
		{
			return ( m_currentParameterType == PropertyType.InstancedProperty ) ? m_outputPorts[ 0 ].LocalValue( portCategory ) : m_propertyName;
		}

		public override void OnNodeLogicUpdate( DrawInfo drawInfo )
		{
			base.OnNodeLogicUpdate( drawInfo );
			if( m_currentParameterType == PropertyType.Global && m_globalDefaultBehavior && ( EditorApplication.timeSinceStartup - m_globalFetchTimestamp ) > MaxGlobalFetchTimestamp )
			{
				FetchGlobalValue();
				m_globalFetchTimestamp = EditorApplication.timeSinceStartup;
			}

			// Keep the property picker hidden while in Reference mode; AfterCommonInit() may run after
			// ReadFromString() or domain reloads and turn it back on
			if( m_canBeReferenced && m_freeType )
			{
				m_hasLeftDropdown = !IsPropertyReference;
			}

			if( IsPropertyReference )
			{
				PropertyNode reference = PropertyReference;
				if( reference != null )
				{
					CopyPropertyReferenceValues( reference );
					string referenceValue = reference.GetPropertyValStr();
					if( !m_lastPropertyReferenceTitle.Equals( reference.PropertyInspectorName ) || !m_lastPropertyReferenceValue.Equals( referenceValue ) )
					{
						m_lastPropertyReferenceTitle = reference.PropertyInspectorName;
						m_lastPropertyReferenceValue = referenceValue;
						m_propertyNameIsDirty = true;
					}
				}
			}
		}

		public void ShowGlobalValueButton()
		{
			if( GUILayout.Button( "Set Global Value" ) )
			{
				SetGlobalValue();
			}
		}

		public override bool CheckFindText( string text )
		{
			return base.CheckFindText( text ) ||
				m_propertyName.IndexOf( text, StringComparison.CurrentCultureIgnoreCase ) >= 0 ||
				m_propertyInspectorName.IndexOf( text, StringComparison.CurrentCultureIgnoreCase ) >= 0;
		}

		//This should only be used on template internal properties
		public void PropertyNameFromTemplate( TemplateShaderPropertyData data )
		{
			m_propertyName = data.PropertyName;
			m_propertyInspectorName = data.PropertyInspectorName;
		}
		public virtual void GeneratePPSInfo( ref string propertyDeclaration, ref string propertySet ) { }
		public virtual void SetGlobalValue() { }
		public virtual void FetchGlobalValue() { }

		public virtual string PropertyName { get { return m_propertyName; } }
		public virtual string PropertyInspectorName { get { return m_propertyInspectorName; } }
		public bool FreeType { get { return m_freeType; } set { m_freeType = value; } }
		public bool ReRegisterName { get { return m_reRegisterName; } set { m_reRegisterName = value; } }
		public string CustomPrefix { get { return m_customPrefix; } set { m_customPrefix = value; } }
		public override string DataToArray { get { return PropertyInspectorName; } }
		public bool RegisterPropertyOnInstancing { get { return m_registerPropertyOnInstancing; } set { m_registerPropertyOnInstancing = value; } }
		public bool SrpBatcherCompatible { get { return m_srpBatcherCompatible; } }
		public bool AddGlobalToSRPBatcher { get { return m_addGlobalToSRPBatcher; } set { m_addGlobalToSRPBatcher = value; } }
		public bool AutoRegister { get { return m_autoRegister; } set { m_autoRegister = value; } }
	}
}
