// Amplify Shader Editor - Visual Shader Editing Tool
// Copyright (c) Amplify Creations, Lda <info@amplify.pt>

using System;
using UnityEngine;
using System.Collections.Generic;
using UnityEditor;

namespace AmplifyShaderEditor
{
	// @diogo: This class is used as a helper to replace ourl simpler SFs with their scripted node counterparts; for performance reasons.
	public static class ShaderFunctionToNode
	{
		private static Dictionary<string, Type> m_table = new Dictionary<string, Type>
		{
			{ "06a5811c77de878498d3c93c4fd438d5", typeof( Hash22Node ) }
		};

		public static bool IsEligible( string guid )
		{
			return m_table.ContainsKey( guid );
		}

		public static ParentNode Convert( FunctionNode functionNode )
		{
			if ( m_table.TryGetValue( functionNode.FunctionGUID, out Type replacementNodeType ) )
			{
				// Create a new node to replace the function node
				ParentNode replacementNode = ( ParentNode )ScriptableObject.CreateInstance( replacementNodeType );

				// Copy basic parameters
				replacementNode.CopyBasePropertiesFrom( functionNode );

				// Delete the now unused function node
				ScriptableObject.DestroyImmediate( functionNode );

				return replacementNode;
			}
			return null;
		}
	}
}
