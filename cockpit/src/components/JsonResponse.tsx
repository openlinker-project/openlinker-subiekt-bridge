interface JsonResponseProps {
  success?: boolean
  data?: any
  error?: string
  loading?: boolean
}

export function JsonResponse({
  success,
  data,
  error,
  loading,
}: JsonResponseProps) {
  if (loading) {
    return (
      <div className="mt-6 p-4 bg-gray-100 rounded border border-gray-300">
        <div className="text-gray-600">Loading...</div>
      </div>
    )
  }

  if (!data && !error) {
    return null
  }

  const bgColor =
    success === undefined
      ? 'bg-gray-100'
      : success
        ? 'bg-green-50'
        : 'bg-red-50'
  const borderColor =
    success === undefined
      ? 'border-gray-300'
      : success
        ? 'border-green-300'
        : 'border-red-300'
  const textColor =
    success === undefined
      ? 'text-gray-700'
      : success
        ? 'text-green-700'
        : 'text-red-700'

  return (
    <div className={`mt-6 p-4 rounded border ${bgColor} ${borderColor}`}>
      <div className={`font-semibold mb-2 ${textColor}`}>
        {success === undefined
          ? 'Response'
          : success
            ? '✓ Success'
            : '✗ Error'}
      </div>

      {error && (
        <div className="text-red-700 mb-4 text-sm font-mono">
          {error}
        </div>
      )}

      <pre className="text-xs overflow-auto max-h-64 bg-white p-2 rounded border border-gray-200">
        {JSON.stringify(data || error, null, 2)}
      </pre>
    </div>
  )
}
