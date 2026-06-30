import { useState, useEffect } from 'react'
import { api } from '../lib/apiClient'
import { FormLayout, FormGroup, FormInput, FormButton } from '../components/FormLayout'

export default function AuditPage() {
  const [limit, setLimit] = useState(10)
  const [loading, setLoading] = useState(false)
  const [auditLogs, setAuditLogs] = useState<any[]>([])
  const [error, setError] = useState<string | undefined>()

  const handleFetch = async () => {
    setLoading(true)
    setError(undefined)
    try {
      const result = await api.auditLast(limit)
      if (result.success) {
        const items = (result.data as any)?.items || []
        setAuditLogs(items)
        if (items.length === 0) {
          setError('No audit logs found')
        }
      } else {
        setError(result.error || 'Failed to load audit logs')
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Unknown error'
      setError(msg)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    handleFetch()
  }, [])

  return (
    <FormLayout title="Audit Log">
      <div className="mb-4">
        <FormGroup label="Limit">
          <FormInput
            type="number"
            value={limit}
            onChange={(e) => setLimit(parseInt(e.target.value) || 10)}
            min={1}
            max={100}
          />
        </FormGroup>
        <FormButton onClick={handleFetch} loading={loading}>
          Refresh
        </FormButton>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded mb-4">
          {error}
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="w-full border-collapse border border-gray-300">
          <thead className="bg-gray-100">
            <tr>
              <th className="border border-gray-300 px-3 py-2 text-left">Timestamp</th>
              <th className="border border-gray-300 px-3 py-2 text-left">Operation</th>
              <th className="border border-gray-300 px-3 py-2 text-left">Status</th>
              <th className="border border-gray-300 px-3 py-2 text-left">Input</th>
              <th className="border border-gray-300 px-3 py-2 text-left">Output</th>
              <th className="border border-gray-300 px-3 py-2 text-left">Error</th>
            </tr>
          </thead>
          <tbody>
            {auditLogs.map((log, idx) => (
              <tr key={idx} className={log.statusCode === 200 ? 'bg-green-50' : 'bg-red-50'}>
                <td className="border border-gray-300 px-3 py-2 text-sm">
                  {new Date(log.timestampUtc).toLocaleString()}
                </td>
                <td className="border border-gray-300 px-3 py-2 text-sm font-semibold">
                  {log.operationType}
                </td>
                <td className="border border-gray-300 px-3 py-2">
                  <span
                    className={`inline-block px-2 py-1 rounded text-sm text-white ${
                      log.statusCode === 200 ? 'bg-green-600' : 'bg-red-600'
                    }`}
                  >
                    {log.statusCode}
                  </span>
                </td>
                <td className="border border-gray-300 px-3 py-2 text-sm max-w-xs overflow-auto">
                  <code className="bg-gray-100 px-2 py-1 rounded block text-xs max-h-12 overflow-y-auto">
                    {log.inputJson ? log.inputJson.substring(0, 100) : '-'}
                  </code>
                </td>
                <td className="border border-gray-300 px-3 py-2 text-sm max-w-xs overflow-auto">
                  <code className="bg-gray-100 px-2 py-1 rounded block text-xs max-h-12 overflow-y-auto">
                    {log.outputJson ? log.outputJson.substring(0, 100) : '-'}
                  </code>
                </td>
                <td className="border border-gray-300 px-3 py-2 text-sm text-red-600">
                  {log.errorMessage || '-'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {auditLogs.length === 0 && !loading && (
        <div className="text-center text-gray-500 py-8">No audit logs found</div>
      )}
    </FormLayout>
  )
}
