using ServiceMaintenance.Services;
using ServiceMaintenance.Models;

namespace ServiceMaintenance.Services.JWT
{
    public interface IMessageService
    {
        /// <summary>
        /// Save a new message
        /// </summary>
        Task SaveMessageAsync(Message message);

        /// <summary>
        /// Get messages between two users
        /// </summary>
        Task<List<Message>> GetMessagesAsync(string userId, string recipientId);

        /// <summary>
        /// Get all messages for a user (both sent and received)
        /// </summary>
        Task<IEnumerable<Message>> GetAllUserMessagesAsync(string userId);

        /// <summary>
        /// Get a specific message by ID
        /// </summary>
        Task<Message> GetMessageByIdAsync(int messageId);

        /// <summary>
        /// Delete a message - returns response with status
        /// </summary>
        Task DeleteMessageAsync(int messageId);

        /// <summary>
        /// Mark messages as read
        /// </summary>
        Task<int> MarkMessagesReadAsync(string recipientId, string senderId);

        /// <summary>
        /// Get unread message count from a specific sender
        /// </summary>
        Task<int> GetUnreadCountAsync(string recipientId, string senderId);

        /// <summary>
        /// Get unread counts from all senders
        /// </summary>
        Task<Dictionary<string, int>> GetUnreadCountsAsync(string recipientId);

        /// <summary>
        /// Get the last message between two users
        /// </summary>
        Task<Message> GetLastMessageAsync(string userId, string recipientId);

        /// <summary>
        /// Delete all messages containing a specific ReportNo (for all users)
        /// </summary>
        Task<int> DeleteMessagesByReportNoAsync(string reportNo);
    }
}