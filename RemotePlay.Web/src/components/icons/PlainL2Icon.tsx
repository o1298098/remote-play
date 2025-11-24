import React, { useMemo } from 'react'

interface PlainL2IconProps {
  className?: string
  style?: React.CSSProperties
  size?: number | string
}

export function PlainL2Icon({ className = '', style = {}, size = 46 }: PlainL2IconProps) {
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
          x="6.627"
          y="6.627"
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
          <feColorMatrix
            type="matrix"
            values="0 0 0 0 1 0 0 0 0 1 0 0 0 0 1 0 0 0 0.2 0"
          />
          <feBlend mode="normal" in2="BackgroundImageFix" result="effect1_dropShadow" />
          <feBlend mode="normal" in="SourceGraphic" in2="effect1_dropShadow" result="shape" />
        </filter>

        {/* inner glow (真实内发光沿边缘模糊) */}
        <filter
          id={filter1}
          x="6.627"
          y="6.627"
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
            d="M11.3137 33.9411C5.06532 27.6927 5.06532 17.5621 11.3137 11.3137C17.5621 5.06532 27.6927 5.06532 33.9411 11.3137L43.1335 20.5061C44.3051 21.6777 44.3051 23.5772 43.1335 24.7487L24.7487 43.1335C23.5772 44.3051 21.6777 44.3051 20.5061 43.1335L11.3137 33.9411Z"
            fill="#7F8A9C"
            fillOpacity="0.01"
          />
        </g>

        <path
          d="M17.2023 27.6279V18.9006H19.0475V26.1066H22.789V27.6279H17.2023ZM23.9598 27.6279V26.2984L27.0663 23.4219C27.3305 23.1663 27.5521 22.9361 27.7311 22.7316C27.9129 22.5271 28.0507 22.3268 28.1445 22.1307C28.2382 21.9319 28.2851 21.7174 28.2851 21.4873C28.2851 21.2316 28.2268 21.0114 28.1104 20.8268C27.9939 20.6393 27.8348 20.4958 27.6331 20.3964C27.4314 20.2941 27.2027 20.243 26.947 20.243C26.68 20.243 26.447 20.2969 26.2481 20.4049C26.0493 20.5128 25.8959 20.6677 25.7879 20.8694C25.68 21.0711 25.626 21.3111 25.626 21.5896H23.8746C23.8746 21.0185 24.0038 20.5228 24.2624 20.1023C24.5209 19.6819 24.8831 19.3566 25.349 19.1265C25.8149 18.8964 26.3518 18.7813 26.9598 18.7813C27.5848 18.7813 28.1288 18.8921 28.5919 19.1137C29.0578 19.3325 29.42 19.6364 29.6785 20.0256C29.9371 20.4148 30.0663 20.8609 30.0663 21.3637C30.0663 21.6932 30.001 22.0185 29.8703 22.3396C29.7425 22.6606 29.5138 23.0171 29.1842 23.4092C28.8547 23.7984 28.3902 24.2657 27.7908 24.8111L26.5166 26.0597V26.1194H30.1814V27.6279H23.9598Z"
          fill="#FBFDFF"
          fillOpacity="0.46"
        />
      </g>
    </svg>
  )
}
