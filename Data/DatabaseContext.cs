using Microsoft.EntityFrameworkCore;
using SyncWatch.Models;

namespace SyncWatch.Data
{
    public class DatabaseContext : DbContext
    {
        public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }

        public DbSet<Movie> Movies { get; set; }
        public DbSet<Room> Rooms { get; set; }
        public DbSet<Participant> Participants { get; set; }
        public DbSet<PlaybackState> PlaybackStates { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Movie>()
                .HasOne(m => m.Room)
                .WithMany(r => r.Movies)
                .HasForeignKey(m => m.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Participant>()
                .HasOne(p => p.Room)
                .WithMany(r => r.Participants)
                .HasForeignKey(p => p.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PlaybackState>()
                .HasKey(p => p.RoomId);

            modelBuilder.Entity<PlaybackState>()
                .HasOne(p => p.Room)
                .WithOne()
                .HasForeignKey<PlaybackState>(p => p.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PlaybackState>()
                .HasOne(p => p.Movie)
                .WithMany()
                .HasForeignKey(p => p.CurrentMovieId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatMessage>()
                .HasOne(m => m.Room)
                .WithMany(r => r.ChatMessages)
                .HasForeignKey(m => m.RoomId)
                .OnDelete(DeleteBehavior.Cascade);
        }

    }

}
