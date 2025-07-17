using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RevitMCPCommandSet.Commands
{
    public class ModifyPartsCommand : ExternalEventCommandBase
    {
        private ModifyPartsEventHandler _handler => (ModifyPartsEventHandler)Handler;

        /// <summary>
        /// Command name
        /// </summary>
        public override string CommandName => "modify_parts";

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        public ModifyPartsCommand(UIApplication uiApp)
            : base(new ModifyPartsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse part modifications
                var partModifications = new List<PartModification>();
                var partModsArray = parameters["partModifications"] as JArray;

                if (partModsArray == null || !partModsArray.Any())
                    throw new ArgumentNullException(nameof(partModifications), "Part modifications list is empty or null");

                foreach (var partModJson in partModsArray)
                {
                    var partMod = new PartModification
                    {
                        PartId = partModJson["partId"].ToObject<long>(),
                        FaceModifications = new List<FaceModification>()
                    };

                    var faceModsArray = partModJson["faceModifications"] as JArray;
                    if (faceModsArray != null)
                    {
                        foreach (var faceModJson in faceModsArray)
                        {
                            var faceMod = new FaceModification
                            {
                                Offset = faceModJson["offset"].ToObject<double>()
                            };

                            // Parse face selector
                            var selectorJson = faceModJson["faceSelector"];
                            faceMod.FaceSelector = new FaceSelector
                            {
                                Method = selectorJson["method"].ToString(),
                                Tolerance = selectorJson["tolerance"]?.ToObject<double>()
                            };

                            // Parse value based on method
                            var valueToken = selectorJson["value"];
                            if (faceMod.FaceSelector.Method == "normal" && valueToken.Type == JTokenType.Object)
                            {
                                faceMod.FaceSelector.Value = new VectorValue
                                {
                                    X = valueToken["x"].ToObject<double>(),
                                    Y = valueToken["y"].ToObject<double>(),
                                    Z = valueToken["z"].ToObject<double>()
                                };
                            }
                            else if (faceMod.FaceSelector.Method == "index")
                            {
                                faceMod.FaceSelector.Value = valueToken.ToObject<int>();
                            }
                            else
                            {
                                faceMod.FaceSelector.Value = valueToken.ToString();
                            }

                            partMod.FaceModifications.Add(faceMod);
                        }
                    }

                    partModifications.Add(partMod);
                }

                // Parse optional options
                ModificationOptions options = null;
                if (parameters.ContainsKey("options"))
                {
                    options = parameters["options"].ToObject<ModificationOptions>();
                }

                // Set handler parameters
                _handler.SetParameters(partModifications, options);

                // Trigger external event and wait for completion
                if (RaiseAndWaitForCompletion(10000))
                {
                    return _handler.Result;
                }
                else
                {
                    throw new TimeoutException("Modify parts operation timed out");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to modify parts: {ex.Message}");
            }
        }
    }
}