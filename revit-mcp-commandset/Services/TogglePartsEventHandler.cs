using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services
{
    public class TogglePartsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
        /// Element IDs to process (input data)
        /// </summary>
        public List<long> ElementIds { get; private set; }

        /// <summary>
        /// Force create flag (optional input data)
        /// </summary>
        public bool? ForceCreate { get; private set; }

        /// <summary>
        /// Execution result (output data)
        /// </summary>
        public AIResult<TogglePartsResult> Result { get; private set; }

        /// <summary>
        /// Set parameters for parts toggling
        /// </summary>
        public void SetParameters(List<long> elementIds, bool? forceCreate = null)
        {
            ElementIds = elementIds;
            ForceCreate = forceCreate;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var createdParts = new List<long>();
                var deletedParts = new List<long>();
                var failedElements = new List<long>();
                var processedElements = new List<ElementInfo>();

                using (Transaction transaction = new (doc, "Toggle Parts"))
                {
                    transaction.Start();

                    foreach (var elementId in ElementIds)
                    {
                        try
                        {
                            Element element = doc.GetElement(new ElementId(elementId));

                            if (element == null)
                            {
                                failedElements.Add(elementId);
                                continue;
                            }

                            // Check if element can have parts
                            if (!PartUtils.AreElementsValidForCreateParts(doc, [element.Id]))
                            {
                                failedElements.Add(elementId);
                                continue;
                            }

                            // Check if parts already exist
                            bool partsExist = PartUtils.HasAssociatedParts(doc, element.Id);

                            // Determine action based on current state and ForceCreate flag
                            bool shouldCreateParts = ForceCreate ?? !partsExist;

                            if (partsExist && (shouldCreateParts || ForceCreate == true))
                            {
                                // Delete existing parts first
                                var existingParts = PartUtils.GetAssociatedParts(doc, element.Id, true, true);
                                foreach (var partId in existingParts)
                                {
                                    doc.Delete(partId);
                                    deletedParts.Add(partId.Value);
                                }
                            }

                            if (shouldCreateParts)
                            {
                                // Create new parts
                                PartUtils.CreateParts(doc, [element.Id]);

                                // Get the newly created parts
                                var newParts = PartUtils.GetAssociatedParts(doc, element.Id, true, true);
                                foreach (var partId in newParts)
                                {
                                    createdParts.Add(partId.Value);
                                }
                            }
                            else if (partsExist && !ForceCreate.HasValue)
                            {
                                // Toggle mode: delete existing parts
                                var existingParts = PartUtils.GetAssociatedParts(doc, element.Id, true, true);
                                foreach (var partId in existingParts)
                                {
                                    doc.Delete(partId);
                                    deletedParts.Add(partId.Value);
                                }
                            }

                            // Add processed element info
                            processedElements.Add(new ElementInfo
                            {
                                Id = elementId,
                                Name = element.Name,
                                Category = element.Category?.Name ?? "Unknown",
                                Action = shouldCreateParts ? "Created" : "Deleted"
                            });
                            doc.ActiveView.get_Parameter(BuiltInParameter.VIEW_PARTS_VISIBILITY).Set(2);
                        }
                        catch (Exception)
                        {
                            failedElements.Add(elementId);
                            // Log individual element error but continue processing
                        }
                    }

                    transaction.Commit();
                }

                Result = new AIResult<TogglePartsResult>
                {
                    Success = true,
                    Message = $"Successfully processed {processedElements.Count} elements. " +
                              $"Created parts for {createdParts.Count} elements, " +
                              $"deleted parts for {deletedParts.Count} elements, " +
                              $"failed to process {failedElements.Count} elements.",
                    Response = new TogglePartsResult
                    {
                        ProcessedElements = processedElements,
                        CreatedPartIds = createdParts,
                        DeletedPartIds = deletedParts,
                        FailedElementIds = failedElements
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<TogglePartsResult>
                {
                    Success = false,
                    Message = $"Error toggling parts: {ex.Message}",
                    Response = new TogglePartsResult
                    {
                        ProcessedElements = [],
                        CreatedPartIds = [],
                        DeletedPartIds = [],
                        FailedElementIds = ElementIds
                    }
                };
                TaskDialog.Show("Error", $"Error toggling parts: {ex.Message}");
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
            return "Toggle Parts";
        }
    }

    /// <summary>
    /// Result class for toggle parts operation
    /// </summary>
    public class TogglePartsResult
    {
        public List<ElementInfo> ProcessedElements { get; set; }
        public List<long> CreatedPartIds { get; set; }
        public List<long> DeletedPartIds { get; set; }
        public List<long> FailedElementIds { get; set; }
    }
}