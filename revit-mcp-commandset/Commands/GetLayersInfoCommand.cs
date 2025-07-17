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
    public class GetLayersInfoCommand : ExternalEventCommandBase
    {
        private GetLayersInfoEventHandler _handler => (GetLayersInfoEventHandler)Handler;

        /// <summary>
        /// Command name
        /// </summary>
        public override string CommandName => "get_layers_info";

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        public GetLayersInfoCommand(UIApplication uiApp)
            : base(new GetLayersInfoEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse parameters
                List<long> typeIds = parameters["typeIds"]?.ToObject<List<long>>();
                if (typeIds == null || !typeIds.Any())
                    throw new ArgumentNullException(nameof(typeIds), "Type IDs list is empty or null");

                // Parse optional includeProperties parameter
                LayerPropertiesFilter includeProperties = null;
                if (parameters.ContainsKey("includeProperties"))
                {
                    includeProperties = parameters["includeProperties"].ToObject<LayerPropertiesFilter>();
                }

                // Set handler parameters
                _handler.SetParameters(typeIds, includeProperties);

                // Trigger external event and wait for completion
                if (RaiseAndWaitForCompletion(10000))
                {
                    return _handler.Result;
                }
                else
                {
                    throw new TimeoutException("Get layers info operation timed out");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to get layers info: {ex.Message}");
            }
        }
    }
}