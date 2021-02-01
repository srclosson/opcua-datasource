﻿using Apache.Arrow;
using Microsoft.Data.Analysis;
using Opc.Ua;
using Pluginv2;
using Prediktor.UA.Client;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace plugin_dotnet
{

	public static class Converter
	{


        public static NodeInfo ConvertToNodeInfo(Opc.Ua.Node node, NamespaceTable namespaceTable)
        {
            var nsUrl = namespaceTable.GetString(node.NodeId.NamespaceIndex);
            var nsNodeId = new NSNodeId() { id = node.NodeId.ToString(), namespaceUrl = nsUrl };
            var nid = System.Text.Json.JsonSerializer.Serialize(nsNodeId);
            return new NodeInfo() { browseName = GetQualifiedName(node.BrowseName, namespaceTable), 
                displayName = node.DisplayName?.Text, nodeClass = (uint)node.NodeClass, nodeId = nid };
        }


        public static BrowseResultsEntry ConvertToBrowseResult(ReferenceDescription referenceDescription, NamespaceTable namespaceTable)
		{
            var nsUrl = namespaceTable.GetString(referenceDescription.NodeId.NamespaceIndex);
            var nsNodeId = new NSNodeId() { id = referenceDescription.NodeId.ToString(), namespaceUrl = nsUrl };
            var nid = System.Text.Json.JsonSerializer.Serialize(nsNodeId);
            return new BrowseResultsEntry(
				referenceDescription.DisplayName.ToString(),
                GetQualifiedName(referenceDescription.BrowseName, namespaceTable),
                nid,
				referenceDescription.TypeId,
				referenceDescription.IsForward,
				Convert.ToUInt32(referenceDescription.NodeClass));
		}

        public static string GetNodeIdAsJson(Opc.Ua.NodeId nodeId, NamespaceTable namespaceTable)
        {
            var nsUrl = namespaceTable.GetString(nodeId.NamespaceIndex);
            var nsNodeId = new NSNodeId() { id = nodeId.ToString(), namespaceUrl = nsUrl };
            var nid = System.Text.Json.JsonSerializer.Serialize(nsNodeId);
            return nid;
        }


        public static NodeId GetNodeId(string nid, NamespaceTable namespaceTable)
        {
            NSNodeId nsNodeId;
            NodeId nId;
            try
            {
                nsNodeId = System.Text.Json.JsonSerializer.Deserialize<NSNodeId>(nid);
                nId = NodeId.Parse(nsNodeId.id);
            }
            catch
            {
                return NodeId.Parse(nid);
            }

            var idx = (ushort)namespaceTable.GetIndex(nsNodeId.namespaceUrl);
            if(idx < ushort.MaxValue)
                return new NodeId(nId.Identifier, idx);

            throw new ArgumentException($"Namespace '{nsNodeId.namespaceUrl}' not found");
        }

        internal static Opc.Ua.QualifiedName GetQualifiedName(QualifiedName qm, NamespaceTable namespaceTable)
        {
            ushort nsIdx;
            if (ushort.TryParse(qm.namespaceUrl, out nsIdx))
                return new Opc.Ua.QualifiedName(qm.name, nsIdx);
            var insIdx = string.IsNullOrWhiteSpace(qm.namespaceUrl) ? 0 : namespaceTable.GetIndex(qm.namespaceUrl);
            return new Opc.Ua.QualifiedName(qm.name, (ushort)insIdx);

        }

        internal static QualifiedName GetQualifiedName(Opc.Ua.QualifiedName qm, NamespaceTable namespaceTable)
        {
            var url = namespaceTable.GetString(qm.NamespaceIndex);
            return new QualifiedName() { name = qm.Name, namespaceUrl = url };

        }


        public static Opc.Ua.QualifiedName[] GetBrowsePath(QualifiedName[] browsePath, NamespaceTable namespaceTable)
        {
            var qms = new Opc.Ua.QualifiedName[browsePath.Length];
            for (int i = 0; i < browsePath.Length; i++)
            {
                var bp = browsePath[i];
                var nsIdx = string.IsNullOrWhiteSpace(bp.namespaceUrl) ? 0 : namespaceTable.GetIndex(bp.namespaceUrl);
                qms[i] = new Opc.Ua.QualifiedName(bp.name, (ushort)nsIdx); ;
            }
            return qms;
        }

        private static LiteralOperand GetLiteralOperand(LiteralOp literop, NamespaceTable namespaceTable)
        {
            var nodeId = Converter.GetNodeId(literop.typeId, namespaceTable);
            if (nodeId.NamespaceIndex == 0 && nodeId.IdType == IdType.Numeric)
            {
                var id = Convert.ToInt32(nodeId.Identifier);
                if (id == 17)  // NodeId: TODO use constant.
                {
                    var nodeIdVal = Converter.GetNodeId(literop.value, namespaceTable);
                    return new LiteralOperand(nodeIdVal);
                }
            }
            return new LiteralOperand(literop.value);
        }

        private static SimpleAttributeOperand GetSimpleAttributeOperand(SimpleAttributeOp literop, NamespaceTable namespaceTable)
        {
            NodeId typeId = null;
            if (!string.IsNullOrWhiteSpace(literop.typeId))
            {
                typeId = Converter.GetNodeId(literop.typeId, namespaceTable);
            }
            return new SimpleAttributeOperand(typeId, literop.browsePath.Select(a => Converter.GetQualifiedName(a, namespaceTable)).ToList());
        }


        private static object GetOperand(FilterOperand operand, NamespaceTable namespaceTable)
        {

            switch (operand.type)
            {
                case FilterOperandEnum.Literal:
                    return GetLiteralOperand(JsonSerializer.Deserialize<LiteralOp>(operand.value), namespaceTable);
                case FilterOperandEnum.Element:
                    {
                        var elementOp = JsonSerializer.Deserialize<ElementOp>(operand.value);
                        return new ElementOperand(elementOp.index);
                    }
                case FilterOperandEnum.SimpleAttribute:
                    return GetSimpleAttributeOperand(JsonSerializer.Deserialize<SimpleAttributeOp>(operand.value), namespaceTable);
                default:
                    throw new ArgumentException();
            }
        }

        internal static object[] GetOperands(EventFilter f, NamespaceTable namespaceTable)
        {
            var operands = new object[f.operands.Length];
            for (int i = 0; i < f.operands.Length; i++)
                operands[i] = GetOperand(f.operands[i], namespaceTable);
            return operands;

        }

        internal static Opc.Ua.EventFilter GetEventFilter(OpcUAQuery query, NamespaceTable namespaceTable)
        {
            var eventFilter = new Opc.Ua.EventFilter();
            if (query.eventQuery?.eventColumns != null)
            {
                foreach (var column in query.eventQuery.eventColumns)
                {
                    var bp = Converter.GetBrowsePath(column.browsePath, namespaceTable);
                    var path = SimpleAttributeOperand.Format(bp);
                    eventFilter.AddSelectClause(ObjectTypes.BaseEventType, path, Attributes.Value);
                }
            }


            if (query.eventQuery?.eventFilters != null)
            {
                for (int i = 0; i < query.eventQuery.eventFilters.Length; i++)
                {
                    var filter = query.eventQuery.eventFilters[i];
                    eventFilter.WhereClause.Push(filter.oper, GetOperands(filter, namespaceTable));
                }
            }
            return eventFilter;
        }




    }
}
