import { useState, useEffect } from 'react'
import { api } from '../lib/apiClient'
import { LoadingSpinner } from '../components/LoadingSpinner'
import { JsonResponse } from '../components/JsonResponse'

export default function TowyPage() {
  const [loading, setLoading] = useState(true)
  const [data, setData] = useState<any[]>([])
  const [error, setError] = useState<string | undefined>()

  useEffect(() => {
    const fetch = async () => {
      try {
        const result = await api.towary(100)
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
      <h1 className="text-2xl font-bold mb-6">Towary (Produkty)</h1>

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
                <th className="border border-gray-300 px-4 py-2 text-left">Symbol</th>
                <th className="border border-gray-300 px-4 py-2 text-left">Nazwa</th>
                <th className="border border-gray-300 px-4 py-2 text-left">Cena ewid.</th>
              </tr>
            </thead>
            <tbody>
              {data.map((towar: any, idx) => (
                <tr key={idx} className="hover:bg-gray-50">
                  <td className="border border-gray-300 px-4 py-2">{towar.Id}</td>
                  <td className="border border-gray-300 px-4 py-2 font-mono">{towar.Symbol}</td>
                  <td className="border border-gray-300 px-4 py-2">{towar.Nazwa}</td>
                  <td className="border border-gray-300 px-4 py-2">{towar.CenaEwidencyjna}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.length === 0 && <p className="text-gray-600 mt-4">No towary found.</p>}
        </div>
      )}

      <JsonResponse success={!error} data={data} error={error} />
    </div>
  )
}
