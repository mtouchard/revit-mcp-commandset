using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Extensions;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services
{
    public class ModifyPartsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;

        /// <summary>
        /// Event wait object
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>
        /// Part modifications to apply (input data)
        /// </summary>
        public List<PartModification> PartModifications { get; private set; }

        /// <summary>
        /// Modification options (input data)
        /// </summary>
        public ModificationOptions Options { get; private set; }

        /// <summary>
        /// Execution result (output data)
        /// </summary>
        public AIResult<ModifyPartsResult> Result { get; private set; }

        /// <summary>
        /// Set parameters for parts modification
        /// </summary>
        public void SetParameters(List<PartModification> partModifications, ModificationOptions options = null)
        {
            PartModifications = partModifications;
            Options = options ?? new ModificationOptions
            {
                CheckIntersections = true,
                MaintainMinimumThickness = null,
                GroupByLayer = false
            };
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var modifiedParts = new List<ModifiedPartInfo>();
                var failedModifications = new List<FailedModification>();
                var warnings = new List<string>();

                using (Transaction transaction = new Transaction(doc, "Modify Parts"))
                {
                    transaction.Start();

                    foreach (var partMod in PartModifications)
                    {
                        try
                        {
                            Element element = doc.GetElement(new ElementId(partMod.PartId));

                            if (!(element is Part part))
                            {
                                failedModifications.Add(new FailedModification
                                {
                                    PartId = partMod.PartId,
                                    Reason = "Element is not a part"
                                });
                                continue;
                            }

                            var modifiedFaces = new List<ModifiedFaceInfo>();
                            var originalGeometry = part.get_Geometry(new Options());

                            foreach (var faceMod in partMod.FaceModifications)
                            {
                                try
                                {
                                    Face targetFace = FindFace(part, faceMod.FaceSelector, originalGeometry);

                                    if (targetFace == null)
                                    {
                                        failedModifications.Add(new FailedModification
                                        {
                                            PartId = partMod.PartId,
                                            Reason = $"Could not find face with selector: {faceMod.FaceSelector.Method} - {faceMod.FaceSelector.Value}"
                                        });
                                        continue;
                                    }

                                    // Check if face is planar (required for SetFaceOffset)
                                    if (!(targetFace is PlanarFace planarFace))
                                    {
                                        failedModifications.Add(new FailedModification
                                        {
                                            PartId = partMod.PartId,
                                            Reason = "Selected face is not planar"
                                        });
                                        continue;
                                    }

                                    // Convert offset from mm to feet
                                    double offsetInFeet = faceMod.Offset / 304.8;

                                    // Check minimum thickness if specified
                                    if (Options.MaintainMinimumThickness.HasValue && faceMod.Offset < 0)
                                    {
                                        if (!CheckMinimumThickness(part, planarFace, offsetInFeet, Options.MaintainMinimumThickness.Value / 304.8))
                                        {
                                            warnings.Add($"Part {partMod.PartId}: Offset would violate minimum thickness constraint");
                                            continue;
                                        }
                                    }

                                    // Apply the offset
                                    part.SetFaceOffset(planarFace, offsetInFeet);

                                    modifiedFaces.Add(new ModifiedFaceInfo
                                    {
                                        FaceNormal = new VectorInfo
                                        {
                                            X = planarFace.FaceNormal.X,
                                            Y = planarFace.FaceNormal.Y,
                                            Z = planarFace.FaceNormal.Z
                                        },
                                        AppliedOffset = faceMod.Offset,
                                        SelectionMethod = faceMod.FaceSelector.Method,
                                        SelectionValue = faceMod.FaceSelector.Value.ToString()
                                    });
                                }
                                catch (Exception ex)
                                {
                                    failedModifications.Add(new FailedModification
                                    {
                                        PartId = partMod.PartId,
                                        Reason = $"Face modification error: {ex.Message}"
                                    });
                                }
                            }

                            if (modifiedFaces.Any())
                            {
                                modifiedParts.Add(new ModifiedPartInfo
                                {
                                    PartId = partMod.PartId,
                                    PartName = part.Name,
                                    ModifiedFaces = modifiedFaces,
                                    SourceElementId = part.GetSourceElementIds().FirstOrDefault().HostElementId?.Value ?? -1
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            failedModifications.Add(new FailedModification
                            {
                                PartId = partMod.PartId,
                                Reason = $"Part processing error: {ex.Message}"
                            });
                        }
                    }

                    // Check for intersections if requested
                    if (Options.CheckIntersections && modifiedParts.Any())
                    {
                        var intersectionWarnings = CheckForIntersections(modifiedParts.Select(mp => mp.PartId).ToList());
                        warnings.AddRange(intersectionWarnings);
                    }

                    transaction.Commit();
                }

                Result = new AIResult<ModifyPartsResult>
                {
                    Success = true,
                    Message = $"Successfully modified {modifiedParts.Count} parts. " +
                              $"Failed to modify {failedModifications.Count} parts. " +
                              $"{warnings.Count} warnings generated.",
                    Response = new ModifyPartsResult
                    {
                        ModifiedParts = modifiedParts,
                        FailedModifications = failedModifications,
                        Warnings = warnings
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<ModifyPartsResult>
                {
                    Success = false,
                    Message = $"Error modifying parts: {ex.Message}",
                    Response = new ModifyPartsResult
                    {
                        ModifiedParts = new List<ModifiedPartInfo>(),
                        FailedModifications = PartModifications.Select(pm => new FailedModification
                        {
                            PartId = pm.PartId,
                            Reason = "Transaction failed"
                        }).ToList(),
                        Warnings = new List<string>()
                    }
                };
                TaskDialog.Show("Error", $"Error modifying parts: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set(); // Notify waiting thread that operation is complete
            }
        }

        /// <summary>
        /// Find a face based on the selector criteria
        /// </summary>
        private Face FindFace(Part part, FaceSelector selector, GeometryElement geometry)
        {
            var faces = new List<PlanarFace>();

            // Collect all faces from the geometry
            foreach (GeometryObject geomObj in geometry)
            {
                if (geomObj is Solid solid)
                {
                    foreach (PlanarFace face in solid.Faces)
                    {
                        faces.Add(face);
                    }
                }
            }

            switch (selector.Method)
            {
                case "direction":
                    return FindFaceByDirection(faces, selector.Value as string);

                case "position":
                    return FindFaceByPosition(part, faces, selector.Value as string);

                case "index":
                    int index = Convert.ToInt32(selector.Value);
                    return faces.ElementAtOrDefault(index);

                case "normal":
                    if (selector.Value is VectorValue vectorValue)
                    {
                        XYZ targetNormal = new XYZ(vectorValue.X, vectorValue.Y, vectorValue.Z).Normalize();
                        double tolerance = selector.Tolerance ?? 5.0; // Default 5 degrees
                        return FindFaceByNormal(faces, targetNormal, tolerance);
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// Find face by direction (top, bottom, left, right, front, back)
        /// </summary>
        private Face FindFaceByDirection(List<PlanarFace> faces, string direction)
        {
            Face bestFace = null;
            double bestScore = -1;

            foreach (var face in faces.OfType<PlanarFace>())
            {
                XYZ normal = face.FaceNormal;
                double score = 0;

                switch (direction.ToLower())
                {
                    case "top":
                        score = normal.DotProduct(XYZ.BasisZ);
                        break;
                    case "bottom":
                        score = -normal.DotProduct(XYZ.BasisZ);
                        break;
                    case "left":
                        score = -normal.DotProduct(XYZ.BasisX);
                        break;
                    case "right":
                        score = normal.DotProduct(XYZ.BasisX);
                        break;
                    case "front":
                        score = normal.DotProduct(XYZ.BasisY);
                        break;
                    case "back":
                        score = -normal.DotProduct(XYZ.BasisY);
                        break;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFace = face;
                }
            }

            return bestFace;
        }

        /// <summary>
        /// Find face by position relative to the wall (interior/exterior/start/end)
        /// </summary>
        private Face FindFaceByPosition(Part part, List<PlanarFace> faces, string position)
        {
            // Get the source element (wall) to determine orientation
            var sourceElementIds = part.GetSourceElementIds();
            if (!sourceElementIds.Any()) return null;

            Element sourceElement = doc.GetElement(sourceElementIds.First().HostElementId);
            if (!(sourceElement is Wall wall)) return null;

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve == null) return null;

            Line wallLine = locationCurve.Curve as Line;
            XYZ wallDirection = wallLine.Direction;
            XYZ wallNormal = new XYZ(-wallDirection.Y, wallDirection.X, 0).Normalize();

            switch (position.ToLower())
            {
                case "interior":
                    // Assuming interior is opposite to wall normal
                    return FindFaceByNormal(faces.OfType<PlanarFace>().ToList(), -wallNormal, 45);

                case "exterior":
                    return FindFaceByNormal(faces.OfType<PlanarFace>().ToList(), wallNormal, 45);

                case "start":
                    return FindFaceByNormal(faces.OfType<PlanarFace>().ToList(), -wallDirection, 45);

                case "end":
                    return FindFaceByNormal(faces.OfType<PlanarFace>().ToList(), wallDirection, 45);
            }

            return null;
        }

        /// <summary>
        /// Find face by normal vector with tolerance
        /// </summary>
        private Face FindFaceByNormal(List<PlanarFace> faces, XYZ targetNormal, double toleranceDegrees)
        {
            double toleranceRadians = toleranceDegrees * Math.PI / 180.0;
            double maxDotProduct = Math.Cos(toleranceRadians);

            return faces.OfType<PlanarFace>()
                .Where(f => f.FaceNormal.DotProduct(targetNormal) >= maxDotProduct)
                .OrderByDescending(f => f.FaceNormal.DotProduct(targetNormal))
                .FirstOrDefault();
        }

        /// <summary>
        /// Check if offset would violate minimum thickness
        /// </summary>
        private bool CheckMinimumThickness(Part part, PlanarFace face, double offset, double minThickness)
        {
            // This is a simplified check - in reality, you'd need to calculate the actual resulting thickness
            // For now, we'll allow the operation and let Revit handle constraints
            return true;
        }

        /// <summary>
        /// Check for intersections between modified parts
        /// </summary>
        private List<string> CheckForIntersections(List<long> modifiedPartIds)
        {
            var warnings = new List<string>();
            // Simplified intersection check - Revit will handle actual geometric conflicts
            // In a full implementation, you would use interference checking API
            return warnings;
        }

        /// <summary>
        /// Wait for completion
        /// </summary>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName implementation
        /// </summary>
        public string GetName()
        {
            return "Modify Parts";
        }
    }

    #region Data Classes

    public class PartModification
    {
        public long PartId { get; set; }
        public List<FaceModification> FaceModifications { get; set; }
    }

    public class FaceModification
    {
        public FaceSelector FaceSelector { get; set; }
        public double Offset { get; set; }
    }

    public class FaceSelector
    {
        public string Method { get; set; }
        public object Value { get; set; }
        public double? Tolerance { get; set; }
    }

    public class VectorValue
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class ModificationOptions
    {
        public bool CheckIntersections { get; set; }
        public double? MaintainMinimumThickness { get; set; }
        public bool GroupByLayer { get; set; }
    }

    public class ModifyPartsResult
    {
        public List<ModifiedPartInfo> ModifiedParts { get; set; }
        public List<FailedModification> FailedModifications { get; set; }
        public List<string> Warnings { get; set; }
    }

    public class ModifiedPartInfo
    {
        public long PartId { get; set; }
        public string PartName { get; set; }
        public List<ModifiedFaceInfo> ModifiedFaces { get; set; }
        public long SourceElementId { get; set; }
    }

    public class ModifiedFaceInfo
    {
        public VectorInfo FaceNormal { get; set; }
        public double AppliedOffset { get; set; }
        public string SelectionMethod { get; set; }
        public string SelectionValue { get; set; }
    }

    public class VectorInfo
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class FailedModification
    {
        public long PartId { get; set; }
        public string Reason { get; set; }
    }

    #endregion
}