using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetLayersInfoEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;

        /// <summary>
        /// Event wait object
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new (false);

        /// <summary>
        /// Type IDs to process (input data)
        /// </summary>
        public List<long> TypeIds { get; private set; }

        /// <summary>
        /// Properties to include in response (input data)
        /// </summary>
        public LayerPropertiesFilter IncludeProperties { get; private set; }

        /// <summary>
        /// Execution result (output data)
        /// </summary>
        public AIResult<LayersInfoResult> Result { get; private set; }

        /// <summary>
        /// Set parameters for getting layers info
        /// </summary>
        public void SetParameters(List<long> typeIds, LayerPropertiesFilter includeProperties = null)
        {
            TypeIds = typeIds;
            IncludeProperties = includeProperties ?? new LayerPropertiesFilter
            {
                Material = true,
                Thickness = true,
                Function = true,
                IsCore = true,
                Wraps = true
            };
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var typeInfoList = new List<TypeLayersInfo>();
                var failedTypeIds = new List<long>();

                using (Transaction transaction = new(doc, "Get Layers Info"))
                {
                    // We only need a transaction for certain operations, but it's good practice
                    transaction.Start();

                    foreach (var typeId in TypeIds)
                    {
                        try
                        {
                            Element typeElement = doc.GetElement(new ElementId(typeId));

                            if (typeElement == null)
                            {
                                failedTypeIds.Add(typeId);
                                continue;
                            }

                            CompoundStructure compoundStructure = null;
                            string typeName = typeElement.Name;
                            string categoryName = typeElement.Category?.Name ?? "Unknown";

                            // Get compound structure based on element type
                            if (typeElement is WallType wallType)
                            {
                                compoundStructure = wallType.GetCompoundStructure();
                            }
                            else if (typeElement is FloorType floorType)
                            {
                                compoundStructure = floorType.GetCompoundStructure();
                            }
                            else if (typeElement is RoofType roofType)
                            {
                                compoundStructure = roofType.GetCompoundStructure();
                            }
                            else if (typeElement is CeilingType ceilingType)
                            {
                                compoundStructure = ceilingType.GetCompoundStructure();
                            }
                            else
                            {
                                failedTypeIds.Add(typeId);
                                continue;
                            }

                            if (compoundStructure == null)
                            {
                                failedTypeIds.Add(typeId);
                                continue;
                            }

                            // Extract layer information
                            var layers = new List<LayerInfo>();
                            var compoundLayers = compoundStructure.GetLayers();

                            for (int i = 0; i < compoundLayers.Count; i++)
                            {
                                var layer = compoundLayers[i];
                                var layerInfo = new LayerInfo
                                {
                                    Index = i,
                                    LayerId = i // Layer ID is essentially its index in the compound structure
                                };

                                if (IncludeProperties.Material)
                                {
                                    var materialId = layer.MaterialId;
                                    if (materialId != ElementId.InvalidElementId)
                                    {
                                        var material = doc.GetElement(materialId) as Material;
                                        layerInfo.MaterialInfo = new MaterialInfo
                                        {
                                            Id = materialId.Value,
                                            Name = material?.Name ?? "Unknown",
                                            Class = material?.MaterialClass ?? "Unknown",
                                            Category = material?.MaterialCategory ?? "Unknown"
                                        };
                                    }
                                }

                                if (IncludeProperties.Thickness)
                                {
                                    layerInfo.Thickness = layer.Width * 304.8; // Convert from feet to mm
                                }

                                if (IncludeProperties.Function)
                                {
                                    layerInfo.Function = layer.Function.ToString();
                                }

                                if (IncludeProperties.IsCore)
                                {
                                    layerInfo.IsCore = compoundStructure.IsCoreLayer(i);
                                }

                                if (IncludeProperties.Wraps)
                                {
                                    layerInfo.Wraps = layer.LayerCapFlag;
                                }

                                layers.Add(layerInfo);
                            }

                            // Add type info to result
                            typeInfoList.Add(new TypeLayersInfo
                            {
                                TypeId = typeId,
                                TypeName = typeName,
                                Category = categoryName,
                                NumberOfLayers = layers.Count,
                                TotalThickness = compoundStructure.GetWidth() * 304.8, // Convert to mm
                                Layers = layers,
                                HasCore = compoundStructure.GetFirstCoreLayerIndex() == compoundStructure.GetLastCoreLayerIndex(),
                                CoreBoundaries = new CoreBoundaryInfo
                                {
                                    FirstCoreLayerIndex = compoundStructure.GetFirstCoreLayerIndex(),
                                    LastCoreLayerIndex = compoundStructure.GetLastCoreLayerIndex()
                                }
                            });
                        }
                        catch (Exception)
                        {
                            failedTypeIds.Add(typeId);
                            // Continue processing other types
                        }
                    }

                    transaction.Commit();
                }

                Result = new AIResult<LayersInfoResult>
                {
                    Success = true,
                    Message = $"Successfully retrieved layer information for {typeInfoList.Count} types. " +
                              $"Failed to process {failedTypeIds.Count} types.",
                    Response = new LayersInfoResult
                    {
                        TypeLayersInfoList = typeInfoList,
                        FailedTypeIds = failedTypeIds
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<LayersInfoResult>
                {
                    Success = false,
                    Message = $"Error getting layers info: {ex.Message}",
                    Response = new LayersInfoResult
                    {
                        TypeLayersInfoList = [],
                        FailedTypeIds = TypeIds
                    }
                };
                TaskDialog.Show("Error", $"Error getting layers info: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set(); // Notify waiting thread that operation is complete
            }
        }

        /// <summary>
        /// Wait for completion
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <returns>Whether operation completed before timeout</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName implementation
        /// </summary>
        public string GetName()
        {
            return "Get Layers Info";
        }
    }

    /// <summary>
    /// Filter for layer properties to include
    /// </summary>
    public class LayerPropertiesFilter
    {
        public bool Material { get; set; } = true;
        public bool Thickness { get; set; } = true;
        public bool Function { get; set; } = true;
        public bool IsCore { get; set; } = true;
        public bool Wraps { get; set; } = true;
    }

    /// <summary>
    /// Result class for get layers info operation
    /// </summary>
    public class LayersInfoResult
    {
        public List<TypeLayersInfo> TypeLayersInfoList { get; set; }
        public List<long> FailedTypeIds { get; set; }
    }

    /// <summary>
    /// Information about a type and its layers
    /// </summary>
    public class TypeLayersInfo
    {
        public long TypeId { get; set; }
        public string TypeName { get; set; }
        public string Category { get; set; }
        public int NumberOfLayers { get; set; }
        public double TotalThickness { get; set; } // in mm
        public List<LayerInfo> Layers { get; set; }
        public bool HasCore { get; set; }
        public CoreBoundaryInfo CoreBoundaries { get; set; }
    }

    /// <summary>
    /// Information about a single layer
    /// </summary>
    public class LayerInfo
    {
        public int Index { get; set; }
        public int LayerId { get; set; }
        public MaterialInfo MaterialInfo { get; set; }
        public double Thickness { get; set; } // in mm
        public string Function { get; set; }
        public bool IsCore { get; set; }
        public bool Wraps { get; set; }
    }

    /// <summary>
    /// Material information
    /// </summary>
    public class MaterialInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Class { get; set; }
        public string Category { get; set; }
    }

    /// <summary>
    /// Core boundary information
    /// </summary>
    public class CoreBoundaryInfo
    {
        public int FirstCoreLayerIndex { get; set; }
        public int LastCoreLayerIndex { get; set; }
    }
}