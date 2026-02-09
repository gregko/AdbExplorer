namespace AdbExplorer.Models
{
    public class AndroidDevice
    {
        public string Id { get; set; } = "";
        public string Model { get; set; } = "";
        public string Status { get; set; } = "";
        public bool IsRooted { get; set; } = false;

        public override string ToString()
        {
            return $"{Model} ({Id}) - {Status}";
        }
    }
}
