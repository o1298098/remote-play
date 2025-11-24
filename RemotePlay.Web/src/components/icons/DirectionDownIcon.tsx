import React, { useMemo } from 'react'

interface DirectionDownIconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function DirectionDownIcon({ className = '', style = {}, size }: DirectionDownIconProps) {
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size || 17}
      height={size || 23}
      viewBox="0 0 17 23"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      style={{
        ...style,
        shapeRendering: 'auto',
        textRendering: 'optimizeLegibility',
        imageRendering: 'auto',
      }}
      preserveAspectRatio="xMidYMid meet"
    >
      <defs>
        {/* inner glow */}
        <filter
          id={filter1}
          x="0"
          y="0"
          width="17"
          height="23"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feBlend mode="normal" in="SourceGraphic" in2="BackgroundImageFix" result="shape" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feGaussianBlur stdDeviation="2.5" />
          <feComposite in2="hardAlpha" operator="arithmetic" k2="-1" k3="1" />
          <feColorMatrix type="matrix" values="0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 1 0" />
          <feBlend mode="normal" in2="shape" result="effect1_innerShadow" />
        </filter>
      </defs>

      <g filter={`url(#${filter1})`}>
        <path
          d="M8.46017 -6.35773e-07C6.42229 -6.35773e-07 1.84378 5.0468 1.43332 6.9346C1.02286 8.82239 -0.700987 17.3299 0.31692 20.0551C1.33483 22.7803 5.34143 23 8.46017 23C11.5768 23 15.5855 22.7803 16.6034 20.0551C17.6213 17.3299 16.4064 8.82239 15.996 6.9346C15.5855 5.0468 10.4981 -6.35773e-07 8.46017 -6.35773e-07Z"
          fill="white"
          fillOpacity="0.01"
        />
      </g>
    </svg>
  )
}
