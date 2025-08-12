namespace AdbExplorer.Models
{
    public class AndroidDevice
    {
        public string Id { get; set; } = "";
        public string Model { get; set; } = "";
        public string Status { get; set; } = "";
        
        public override string ToString()
        {
            return $"{Model} ({Id}) - {Status}";
        }
    }
}
