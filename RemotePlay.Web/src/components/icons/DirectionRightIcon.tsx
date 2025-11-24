import React, { useMemo } from 'react'

interface DirectionRightIconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function DirectionRightIcon({ className = '', style = {}, size }: DirectionRightIconProps) {
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size || 23}
      height={size || 17}
      viewBox="0 0 23 17"
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
          width="23"
          height="17"
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
          d="M-6.35773e-07 8.53983C-6.35773e-07 10.5777 5.0468 15.1562 6.9346 15.5667C8.82239 15.9771 17.3299 17.701 20.0551 16.6831C22.7803 15.6652 23 11.6586 23 8.53983C23 5.42316 22.7803 1.41449 20.0551 0.396584C17.3299 -0.621327 8.82239 0.593555 6.9346 1.00402C5.0468 1.41448 -6.35773e-07 6.50195 -6.35773e-07 8.53983Z"
          fill="white"
          fillOpacity="0.01"
        />
      </g>
    </svg>
  )
}
