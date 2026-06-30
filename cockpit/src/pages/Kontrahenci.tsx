import { useState, useEffect } from 'react'
import { api } from '../lib/apiClient'
import { LoadingSpinner } from '../components/LoadingSpinner'
import { JsonResponse } from '../components/JsonResponse'

export default function KontrahenciPage() {
  const [loading, setLoading] = useState(true)
  const [data, setData] = useState<any[]>([])
  const [error, setError] = useState<string | undefined>()

  useEffect(() => {
    const fetch = async () => {
      try {
        const result = await api.kontrahenci(100)
        if (!result.success) {
          setError(result.error)
        } else {
          const items = (result.data as any)?.items || []
          setData(items)
        }
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Unknown error')
      } finally {
        setLoading(false)
      }
    }
    fetch()
  }, [])

  return (
    <div>
      <h1 className="text-2xl font-bold mb-6">Kontrahenci</h1>

      {loading && <LoadingSpinner />}

      {error && (
        <div className="p-4 bg-red-50 border border-red-300 rounded text-red-700">
          Error: {error}
        </div>
      )}

      {data && (
        <div className="overflow-x-auto">
          <table className="min-w-full border-collapse border border-gray-300">
            <thead className="bg-gray-100">
              <tr>
                <th className="border border-gray-300 px-4 py-2 text-left">ID</th>
                <th className="border border-gray-300 px-4 py-2 text-left">Nazwa</th>
                <th className="border border-gray-300 px-4 py-2 text-left">NIP</th>
                <th className="border border-gray-300 px-4 py-2 text-left">Telefon</th>
                <th className="border border-gray-300 px-4 py-2 text-left">Aktywny</th>
              </tr>
            </thead>
            <tbody>
              {data.map((k: any, idx) => (
                <tr key={idx} className="hover:bg-gray-50">
                  <td className="border border-gray-300 px-4 py-2">{k.Id}</td>
                  <td className="border border-gray-300 px-4 py-2">{k.NazwaSkrocona}</td>
                  <td className="border border-gray-300 px-4 py-2 font-mono">{k.NIP || '-'}</td>
                  <td className="border border-gray-300 px-4 py-2">{k.Telefon || '-'}</td>
                  <td className="border border-gray-300 px-4 py-2">{k.Aktywny ? '✓' : '✗'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.length === 0 && <p className="text-gray-600 mt-4">No kontrahenci found.</p>}
        </div>
      )}

      <JsonResponse success={!error} data={data} error={error} />
    </div>
  )
}
