import React, { useMemo } from 'react'

interface DirectionUpIconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function DirectionUpIcon({ className = '', style = {}, size }: DirectionUpIconProps) {
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
          d="M8.53983 23C10.5777 23 15.1562 17.9532 15.5667 16.0654C15.9771 14.1776 17.701 5.67014 16.6831 2.94493C15.6652 0.219718 11.6586 0 8.53983 0C5.42316 0 1.41449 0.219718 0.396584 2.94493C-0.621327 5.67014 0.593555 14.1776 1.00402 16.0654C1.41448 17.9532 6.50195 23 8.53983 23Z"
          fill="white"
          fillOpacity="0.01"
        />
      </g>
    </svg>
  )
}
