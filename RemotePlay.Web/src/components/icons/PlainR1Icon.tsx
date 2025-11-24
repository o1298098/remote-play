import React, { useMemo } from 'react'

interface PlainR1IconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function PlainR1Icon({ className = '', style = {}, size = 46 }: PlainR1IconProps) {
  const filter0 = useMemo(() => `f0-${Math.random().toString(36).slice(2)}`, [])
  const filter1 = useMemo(() => `f1-${Math.random().toString(36).slice(2)}`, [])

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 46 46"
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
        {/* drop shadow */}
        <filter
          id={filter0}
          x="6.62742"
          y="1.24264"
          width="37.3848"
          height="37.3848"
          filterUnits="userSpaceOnUse"
          colorInterpolationFilters="sRGB"
          filterRes="500 500"
        >
          <feFlood floodOpacity="0" result="BackgroundImageFix" />
          <feColorMatrix
            in="SourceAlpha"
            type="matrix"
            values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
            result="hardAlpha"
          />
          <feOffset />
          <feComposite in2="hardAlpha" operator="out" />
          <feColorMatrix type="matrix" values="0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 0.2 0" />
          <feBlend mode="normal" in2="BackgroundImageFix" result="effect1_dropShadow" />
          <feBlend mode="normal" in="SourceGraphic" in2="effect1_dropShadow" result="shape" />
        </filter>

        {/* inner glow */}
        <filter
          id={filter1}
          x="6.62742"
          y="1.24264"
          width="37.3848"
          height="37.3848"
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
          <feColorMatrix
            type="matrix"
            values="0 0 0 0 0.985577 0 0 0 0 0.991048 0 0 0 0 1 0 0 0 1 0"
          />
          <feBlend mode="normal" in2="shape" result="effect1_innerShadow" />
        </filter>
      </defs>

      <g filter={`url(#${filter0})`}>
        <g filter={`url(#${filter1})`}>
          <path
            d="M33.9411 33.9411C27.6927 40.1895 17.5621 40.1895 11.3137 33.9411C5.06532 27.6927 5.06532 17.5621 11.3137 11.3137L20.5061 2.12132C21.6777 0.949747 23.5772 0.949747 24.7487 2.12132L43.1335 20.5061C44.3051 21.6777 44.3051 23.5772 43.1335 24.7487L33.9411 33.9411Z"
            fill="#7F8A9C"
            fillOpacity="0.01"
          />
        </g>

        <path
          d="M18.0129 26.6279V17.9006H21.4561C22.1151 17.9006 22.6776 18.0185 23.1436 18.2543C23.6123 18.4873 23.9688 18.8182 24.2132 19.2472C24.4603 19.6734 24.5839 20.1748 24.5839 20.7515C24.5839 21.331 24.4589 21.8296 24.2089 22.2472C23.9589 22.662 23.5967 22.9802 23.1222 23.2018C22.6507 23.4234 22.0796 23.5342 21.4092 23.5342H19.1038V22.0512H21.1109C21.4632 22.0512 21.7558 22.0029 21.9887 21.9063C22.2217 21.8097 22.395 21.6648 22.5086 21.4717C22.6251 21.2785 22.6833 21.0384 22.6833 20.7515C22.6833 20.4617 22.6251 20.2174 22.5086 20.0185C22.395 19.8197 22.2203 19.6691 21.9845 19.5668C21.7515 19.4617 21.4575 19.4092 21.1024 19.4092H19.858V26.6279H18.0129ZM22.7259 22.6563L24.895 26.6279H22.858L20.7359 22.6563H22.7259ZM29.6336 17.9006V26.6279H27.7884V19.6521H27.7373L25.7387 20.9049V19.2685L27.8992 17.9006H29.6336Z"
          fill="#FBFDFF"
          fillOpacity="0.46"
        />
      </g>
    </svg>
  )
}
