import React, { useMemo } from 'react'

interface DirectionLeftIconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function DirectionLeftIcon({ className = '', style = {}, size }: DirectionLeftIconProps) {
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
          d="M23 8.46017C23 6.42229 17.9532 1.84378 16.0654 1.43332C14.1776 1.02286 5.67014 -0.700987 2.94493 0.31692C0.219718 1.33483 0 5.34143 0 8.46017C0 11.5768 0.219718 15.5855 2.94493 16.6034C5.67014 17.6213 14.1776 16.4064 16.0654 15.996C17.9532 15.5855 23 10.4981 23 8.46017Z"
          fill="white"
          fillOpacity="0.01"
        />
      </g>
    </svg>
  )
}
