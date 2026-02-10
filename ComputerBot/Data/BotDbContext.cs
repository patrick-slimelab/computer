using Microsoft.EntityFrameworkCore;

namespace ComputerBot.Data
{
    public class BotDbContext : DbContext
    {
        public DbSet<HandledEvent> HandledEvents { get; set; }
        public DbSet<ImageChannelMapping> ImageChannelMappings { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=data/computer.db");
        }
    }

    public class HandledEvent
    {
        public int Id { get; set; }
        public string EventId { get; set; }
        public DateTime ProcessedAt { get; set; }
    }

    public class ImageChannelMapping
    {
        public int Id { get; set; }
        public string SourceRoomId { get; set; }
        public string TargetRoomId { get; set; }
    }
}
