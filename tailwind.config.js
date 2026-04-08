// Tailwind CLI config — scans Server wwwroot HTML and JS files for class names.
module.exports = {
    darkMode: 'class',
    content: [
        './src/QBModsBrowser.Server/wwwroot/**/*.html',
        './src/QBModsBrowser.Server/wwwroot/**/*.js',
    ],
    theme: {
        extend: {
            colors: {
                surface: { DEFAULT: '#111827', alt: '#1f2937', raised: '#374151' },
                accent: { DEFAULT: '#3b82f6', hover: '#2563eb' },
            },
        },
    },
    plugins: [],
};
