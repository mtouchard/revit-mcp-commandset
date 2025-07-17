using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands
{
    public class TogglePartsCommand : ExternalEventCommandBase
    {
        private TogglePartsEventHandler _handler => (TogglePartsEventHandler)Handler;

        /// <summary>
        /// Command name
        /// </summary>
        public override string CommandName => "toggle_parts";

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="uiApp">Revit UIApplication</param>
        public TogglePartsCommand(UIApplication uiApp)
            : base(new TogglePartsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // Parse parameters
                List<long> elementIds = parameters["elementIds"]?.ToObject<List<long>>();
                if (elementIds == null || !elementIds.Any())
                    throw new ArgumentNullException(nameof(elementIds), "Element IDs list is empty or null");

                // Parse optional forceCreate parameter
                bool? forceCreate = null;
                if (parameters.ContainsKey("forceCreate"))
                {
                    forceCreate = parameters["forceCreate"].ToObject<bool>();
                }

                // Set handler parameters
                _handler.SetParameters(elementIds, forceCreate);

                // Trigger external event and wait for completion
                if (RaiseAndWaitForCompletion(10000))
                {
                    return _handler.Result;
                }
                else
                {
                    throw new TimeoutException("Toggle parts operation timed out");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to toggle parts: {ex.Message}");
            }
        }
    }
}