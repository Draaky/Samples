using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using TrafficMonitor.Model;
using TrafficMonitor.Services;

namespace TrafficMonitorFunctionApp.Functions
{
    public class CheckSectionControl
    {
        private readonly IStorage storage;

        public CheckSectionControl(IStorage storage)
        {
            this.storage = storage;
        }

        /// <summary>
        /// Speed check for section control
        /// </summary>
        [FunctionName("CheckSectionControl")]
        public async Task Run(
            [ServiceBusTrigger("plate-read", "section-control", Connection = "SECCTRL_RECEIVE_PLATE_READ")]PlateRead plate,
            ILogger log)
        {
            log.LogInformation($"Start section control check for {plate.LicensePlate}");

            // Get car and camera from DB
            log.LogInformation($"Get car and camera data from database");
            var car = await storage.GetCarByLicensePlateAsync(plate.Nationality, plate.LicensePlate);
            if (car == null)
            {
                log.LogWarning($"Unknown car {plate.Nationality} {plate.LicensePlate}");
                return;
            }

            var camera = await storage.GetCameraByIDAsync(plate.CameraID);
            if (camera == null)
            {
                log.LogWarning($"Unknown camera {plate.CameraID}");
                return;
            }

            if (camera.Start != null)
            {
                // License plate read is from a start of a section control
                log.LogInformation("Processing entry into section control");

                if (car.ActiveSection != null)
                {
                    car.Violations.Add(new Violation(plate.ReadTimestamp, "Possible fraud, entered multiple times"));
                }
                else
                {
                    car.ActiveSection = new Enter { StartCameraID = camera.ID, Timestamp = plate.ReadTimestamp };
                }

            }
            else if (camera.End != null)
            {
                // License plate read is from an end of a section control
                log.LogInformation("Processing exit from section control");

                if (car.ActiveSection == null)
                {
                    car.Violations.Add(new Violation(plate.ReadTimestamp, "Possible fraud, exit without enter"));
                }
                else if (camera.End.StartCameraID != car.ActiveSection.StartCameraID)
                {
                    car.Violations.Add(new Violation(plate.ReadTimestamp, "Possible fraud, exit and start do not belong th the same section control"));
                }
                else
                {
                    var averageSpeed = ((double)camera.End.DistanceFromStart) / 1000 / TimeSpan.FromTicks(plate.ReadTimestamp - car.ActiveSection.Timestamp).TotalHours;
                    if (averageSpeed > camera.End.MaximumAverageSpeed * 1.1d)
                    {
                        car.Violations.Add(new Violation(plate.ReadTimestamp, "Too fast"));
                    }

                    car.ActiveSection = null;
                }
            }

            await storage.UpdateCarAsync(car);
        }
    }
}
