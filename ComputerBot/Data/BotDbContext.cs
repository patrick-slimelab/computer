using Microsoft.EntityFrameworkCore;

namespace ComputerBot.Data
{
    public class BotDbContext : DbContext
    {
        public DbSet<HandledEvent> HandledEvents { get; set; }

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
}
