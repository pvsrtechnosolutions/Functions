using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Invoicegeni.Functions
{
    public static class DbHelper
    {
        /// <summary>
        /// Gets the ID from a table based on a SELECT query. If not found, executes the insertQuery to insert and returns new ID.
        /// </summary>
        public static int GetOrInsert(
            SqlConnection conn,
            SqlTransaction transaction,
            string selectQuery,
            string insertQuery,
            Action<SqlCommand> addParameters)
        {
            // Try to get existing ID
            using (var cmd = new SqlCommand(selectQuery, conn, transaction))
            {
                addParameters(cmd);
                var result = cmd.ExecuteScalar();
                if (result != null && int.TryParse(result.ToString(), out int id))
                {
                    return id;
                }
            }

            // Insert new record and return new ID
            using (var cmd = new SqlCommand(insertQuery, conn, transaction))
            {
                addParameters(cmd);
                return (int)cmd.ExecuteScalar();
            }
        }
    }
}
